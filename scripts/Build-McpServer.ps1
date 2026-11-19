<#
.SYNOPSIS
    Builds the SolidWorksMcpServer (.NET Framework 4.8 x64) MCP stdio server executable.
    Automatically moves aside locked executables if an active MCP client holds a handle.
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir
$csproj = Join-Path $repoRoot "src\SolidWorksMcpServer\SolidWorksMcpServer.csproj"
$outDir = Join-Path $repoRoot "src\SolidWorksMcpServer\bin\x64\$Configuration"
$outExe = Join-Path $outDir "SolidWorksMcpServer.exe"
$outPdb = Join-Path $outDir "SolidWorksMcpServer.pdb"

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
    throw "Could not find a valid MSBuild executable."
}

Write-Host "Using MSBuild: $msbuild" -ForegroundColor Cyan

if (Test-Path $outDir) {
    Get-ChildItem $outDir -Filter "*.bak*" -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
    }
    foreach ($locked in @($outExe, $outPdb)) {
        if (Test-Path $locked) {
            $bak = "$locked.bak.$([DateTime]::Now.Ticks)"
            try {
                Move-Item -Path $locked -Destination $bak -Force -ErrorAction Stop
                Remove-Item -Path $bak -Force -ErrorAction SilentlyContinue
            } catch { }
        }
    }
}

Write-Host "Building $csproj ($Configuration|x64)..." -ForegroundColor Cyan
& $msbuild $csproj /p:Configuration=$Configuration /p:Platform=x64 /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Build failed for SolidWorksMcpServer with exit code $LASTEXITCODE"
}

if (-not (Test-Path $outExe)) {
    throw "Expected output executable not found: $outExe"
}

Write-Host "Build SUCCESS: $outExe" -ForegroundColor Green
