<#
.SYNOPSIS
    Builds the SolidWorksJoineryAddin and SolidWorks3DPrintDFMAddin C# projects.
    Automatically renames locked DLLs (.dll.bak) if SolidWorks is still running in the background.
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Project = "",
    [string[]]$Projects = @()
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir

$allProjects = @(
    @{
        Name = "SolidWorksJoineryAddin"
        Csproj = Join-Path $repoRoot "src\SolidWorksJoineryAddin\SolidWorksJoineryAddin.csproj"
        OutputDll = Join-Path $repoRoot "src\SolidWorksJoineryAddin\bin\x64\$Configuration\SolidWorksJoineryAddin.dll"
    },
    @{
        Name = "SolidWorks3DPrintDFMAddin"
        Csproj = Join-Path $repoRoot "src\SolidWorks3DPrintDFMAddin\SolidWorks3DPrintDFMAddin.csproj"
        OutputDll = Join-Path $repoRoot "src\SolidWorks3DPrintDFMAddin\bin\x64\$Configuration\SolidWorks3DPrintDFMAddin.dll"
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

$targetProjects = $allProjects
if ($requestedProjects.Count -gt 0) {
    $validNames = @($allProjects | ForEach-Object { $_.Name })
    $unknown = @($requestedProjects | Where-Object { $validNames -inotcontains $_ })
    if ($unknown.Count -gt 0) {
        Write-Error "Unknown project(s): $($unknown -join ', '). Valid values: $($validNames -join ', ')"
    }
    $targetProjects = @($allProjects | Where-Object { $requestedProjects -icontains $_.Name })
}

# Find MSBuild
$msbuildCandidates = @(
    "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe",
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
)

$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $msbuild) {
    Write-Error "Could not find a valid MSBuild executable."
}

Write-Host "Using MSBuild: $msbuild" -ForegroundColor Cyan

foreach ($proj in $targetProjects) {
    $dll = $proj.OutputDll
    $pdb = [System.IO.Path]::ChangeExtension($dll, ".pdb")

    # Clean up old .bak files if unlocked, and move any currently locked .dll/.pdb aside so MSBuild never blocks
    Get-ChildItem (Split-Path $dll -Parent) -Filter "*.bak*" -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $dll) {
        $bakDll = "$dll.bak.$([DateTime]::Now.Ticks)"
        try {
            Move-Item -Path $dll -Destination $bakDll -Force -ErrorAction Stop
            Remove-Item -Path $bakDll -Force -ErrorAction SilentlyContinue
        } catch { }
    }
    if (Test-Path $pdb) {
        $bakPdb = "$pdb.bak.$([DateTime]::Now.Ticks)"
        try {
            Move-Item -Path $pdb -Destination $bakPdb -Force -ErrorAction Stop
            Remove-Item -Path $bakPdb -Force -ErrorAction SilentlyContinue
        } catch { }
    }

    Write-Host "Building project: $($proj.Csproj) ($Configuration|x64)" -ForegroundColor Cyan
    & $msbuild $proj.Csproj /p:Configuration=$Configuration /p:Platform=x64 /v:minimal

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed for $($proj.Name) with exit code $LASTEXITCODE"
    }

    if (Test-Path $proj.OutputDll) {
        Write-Host "Build SUCCESS: $($proj.OutputDll)" -ForegroundColor Green
    } else {
        Write-Error "Output DLL not found: $($proj.OutputDll)"
    }
}
