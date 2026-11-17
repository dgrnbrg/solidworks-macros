<#
.SYNOPSIS
    Registers or unregisters SolidWorksJoineryAddin and SolidWorks3DPrintDFMAddin COM DLLs with SolidWorks.
    Automatically elevates via Windows UAC if not already running as Administrator, because SolidWorks
    only enumerates Add-Ins registered under HKEY_LOCAL_MACHINE\SOFTWARE\SolidWorks\AddIns.
#>
[CmdletBinding()]
param(
    [switch]$Unregister,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Project = "",
    [string[]]$Projects = @()
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir

$addins = @(
    @{
        Name = "SolidWorksJoineryAddin"
        Guid = "{C924295D-2877-4D90-95FE-3BE5C2A5405A}"
        Title = "Multi-Body Joinery Generator"
        Description = "Parametric Multi-Body Tab & Slot, Finger Joint, and Dado generator with Diagonal Dogbones and Poka-Yoke Keying"
        DllPath = Join-Path $repoRoot "src\SolidWorksJoineryAddin\bin\x64\$Configuration\SolidWorksJoineryAddin.dll"
    },
    @{
        Name = "SolidWorks3DPrintDFMAddin"
        Guid = "{3E9A4B71-6D2C-4F18-8E53-9A7C1D4B206F}"
        Title = "3D Print DFM (Teardrops & Crisscross Bridges)"
        Description = "Automated 40 deg Capped Teardrop Crown Reliefs and Integral 2-Tier Crisscross Bridges for 3D Printing"
        DllPath = Join-Path $repoRoot "src\SolidWorks3DPrintDFMAddin\bin\x64\$Configuration\SolidWorks3DPrintDFMAddin.dll"
    }
)

$requestedProjects = @()
if (-not [string]::IsNullOrWhiteSpace($Project)) {
    $requestedProjects += ($Project -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
}
if ($Projects -and $Projects.Count -gt 0) {
    foreach ($p in $Projects) {
        if (-not [string]::IsNullOrWhiteSpace($p)) {
            $requestedProjects += ($p -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
        }
    }
}

if ($requestedProjects.Count -gt 0) {
    $validNames = @($addins | ForEach-Object { $_.Name })
    $unknown = @($requestedProjects | Where-Object { $validNames -inotcontains $_ })
    if ($unknown.Count -gt 0) {
        Write-Error "Unknown add-in(s): $($unknown -join ', '). Valid values: $($validNames -join ', ')"
    }
    $addins = @($addins | Where-Object { $requestedProjects -icontains $_.Name })
}

# Check for Administrator elevation; if not elevated, re-launch elevated via UAC
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "SolidWorks requires Administrator privileges to register Add-Ins in HKLM. Requesting UAC elevation..." -ForegroundColor Yellow
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Configuration $Configuration"
    if ($Unregister) {
        $argList += " -Unregister"
    }
    if ($requestedProjects.Count -gt 0) {
        $argList += " -Project `"$($requestedProjects -join ',')`""
    }
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs -Wait -PassThru
    exit $proc.ExitCode
}

$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"
if (-not (Test-Path $regasm)) {
    Write-Error "regasm.exe not found at: $regasm"
}

foreach ($addin in $addins) {
    $dllPath = $addin.DllPath
    $addinGuid = $addin.Guid

    if (-not (Test-Path $dllPath)) {
        Write-Error "DLL not found at: $dllPath. Please run scripts\Build-Addin.ps1 first."
    }

    if ($Unregister) {
        Write-Host "Unregistering $($addin.Name)..." -ForegroundColor Yellow
        & $regasm /u "$dllPath"
        & reg.exe delete "HKLM\SOFTWARE\SolidWorks\AddIns\$addinGuid" /f 2>$null | Out-Null
        & reg.exe delete "HKLM\SOFTWARE\SolidWorks\SOLIDWORKS 2026\Addins\$addinGuid" /f 2>$null | Out-Null
        & reg.exe delete "HKCU\Software\SolidWorks\AddInsStartup\$addinGuid" /f 2>$null | Out-Null
        & reg.exe delete "HKCU\Software\SolidWorks\AddIns\$addinGuid" /f 2>$null | Out-Null
        Write-Host "Unregistered $($addin.Name) successfully." -ForegroundColor Green
    } else {
        Write-Host "Registering $($addin.Name) in HKLM..." -ForegroundColor Cyan

        # 1. Register COM classes and execute [ComRegisterFunction] via 64-bit regasm
        & $regasm /codebase "$dllPath"
        if ($LASTEXITCODE -ne 0) {
            Write-Error "regasm failed for $($addin.Name) with exit code $LASTEXITCODE"
        }

        # 2. Explicitly write HKLM\SOFTWARE\SolidWorks\AddIns\<GUID> and SOLIDWORKS 2026\Addins\<GUID>
        $hklmKeys = @(
            "HKLM\SOFTWARE\SolidWorks\AddIns\$addinGuid",
            "HKLM\SOFTWARE\SolidWorks\SOLIDWORKS 2026\Addins\$addinGuid"
        )

        foreach ($regKey in $hklmKeys) {
            & reg.exe add $regKey /ve /t REG_DWORD /d 1 /f | Out-Null
            & reg.exe add $regKey /v "Default" /t REG_DWORD /d 1 /f | Out-Null
            & reg.exe add $regKey /v "Title" /t REG_SZ /d $addin.Title /f | Out-Null
            & reg.exe add $regKey /v "Description" /t REG_SZ /d $addin.Description /f | Out-Null
        }

        # 3. Enable in HKCU\Software\SolidWorks\AddInsStartup\<GUID> so it loads on startup
        & reg.exe add "HKCU\Software\SolidWorks\AddInsStartup\$addinGuid" /ve /t REG_DWORD /d 1 /f | Out-Null

        Write-Host "Registration SUCCESS: $($addin.Title) -> $addinGuid" -ForegroundColor Green
    }
}
