<#
.SYNOPSIS
Interactive / double-clickable installer for ExportFacesToDXF.swp.

.DESCRIPTION
Compiles the latest ExportFacesToDXF.bas into ExportFacesToDXF.swp.
Checks for file locks (prompting user to close VBE/SOLIDWORKS if locked),
and if SOLIDWORKS is running, offers to execute live COM compile verification.

.PARAMETER SourceBas
Path to source .bas file. Defaults to <repo>\ExportFacesToDXF.bas.

.PARAMETER OutputSwp
Path to output .swp file. Defaults to <repo>\ExportFacesToDXF.swp.

.PARAMETER RunLiveTest
Automatically run live SOLIDWORKS COM tests after compiling.

.PARAMETER NonInteractive
Run without pausing or prompting for interactive input.
#>
[CmdletBinding()]
param(
    [string]$SourceBas,
    [string]$OutputSwp,
    [switch]$RunLiveTest,
    [switch]$NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceBas)) {
    $SourceBas = Join-Path $repoRoot "ExportFacesToDXF.bas"
}
if ([string]::IsNullOrWhiteSpace($OutputSwp)) {
    $OutputSwp = Join-Path $repoRoot "ExportFacesToDXF.swp"
}

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "         SOLIDWORKS Macro Installer: ExportFacesToDXF" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path -LiteralPath $SourceBas)) {
    Write-Host "[ERROR] Source file not found: $SourceBas" -ForegroundColor Red
    if (-not $NonInteractive) { Read-Host "Press Enter to exit" }
    exit 1
}

# Check for file locks on target .swp
if (Test-Path -LiteralPath $OutputSwp) {
    $isLocked = $true
    while ($isLocked) {
        try {
            $fs = [System.IO.File]::Open($OutputSwp, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $fs.Close()
            $isLocked = $false
        } catch {
            Write-Host ""
            Write-Host "[LOCKED] '$OutputSwp' is currently open or locked by SOLIDWORKS." -ForegroundColor Yellow
            Write-Host "         To release the file lock:" -ForegroundColor Yellow
            Write-Host "           1. In SOLIDWORKS, close the VBA Editor (Alt+F11) and close any running macros." -ForegroundColor Yellow
            Write-Host "           2. If still locked, save your open documents and restart SOLIDWORKS." -ForegroundColor Yellow
            Write-Host ""
            if ($NonInteractive) {
                Write-Host "[ERROR] Cannot overwrite locked file in non-interactive mode." -ForegroundColor Red
                exit 1
            }
            $choice = Read-Host "Press [Enter] to retry after closing, or type 'q' and press [Enter] to quit"
            if ($choice -match '^[qQ]') {
                Write-Host "Installation cancelled." -ForegroundColor Yellow
                exit 1
            }
        }
    }
}

# Compile / update .swp
Write-Host "Compiling '$OutputSwp' from source..." -ForegroundColor Cyan
$buildScript = Join-Path $PSScriptRoot "Build-Swp.ps1"
try {
    & $buildScript -SourceBas $SourceBas -OutputSwp $OutputSwp
} catch {
    Write-Host "[ERROR] Failed to compile .swp: $($_.Exception.Message)" -ForegroundColor Red
    if (-not $NonInteractive) { Read-Host "Press Enter to exit" }
    exit 1
}

$swpItem = Get-Item -LiteralPath $OutputSwp
$sizeKb = [Math]::Round($swpItem.Length / 1024, 1)
Write-Host "[OK] Successfully updated: $($swpItem.FullName) ($sizeKb KB)" -ForegroundColor Green

# Check if SOLIDWORKS is running
$swRunning = $false
try {
    $proc = Get-Process sldworks -ErrorAction SilentlyContinue
    if ($null -ne $proc) { $swRunning = $true }
} catch {}
if (-not $swRunning) {
    try {
        $comObj = [System.Runtime.InteropServices.Marshal]::GetActiveObject("SldWorks.Application")
        if ($null -ne $comObj) { $swRunning = $true }
    } catch {}
}

if ($swRunning) {
    Write-Host ""
    Write-Host "[INFO] Detected running SOLIDWORKS session." -ForegroundColor Green
    $doTest = $RunLiveTest
    if (-not $RunLiveTest -and -not $NonInteractive) {
        $ans = Read-Host "Would you like to run the automated compile verification test now? [Y/n]"
        if ([string]::IsNullOrWhiteSpace($ans) -or $ans -match '^[yY]') {
            $doTest = $true
        }
    }
    if ($doTest) {
        Write-Host "Running live COM verification test..." -ForegroundColor Cyan
        $testScript = Join-Path $PSScriptRoot "Test-SolidWorksCom.ps1"
        try {
            & $testScript -SwpPath $OutputSwp -SkipBuild
            Write-Host "[OK] Live COM test passed successfully!" -ForegroundColor Green
        } catch {
            Write-Host "[WARNING] Live COM test failed: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Write-Host "======================================================================" -ForegroundColor Green
Write-Host "  Installation Complete!" -ForegroundColor Green
Write-Host "======================================================================" -ForegroundColor Green
Write-Host "Macro File:" -ForegroundColor White
Write-Host "  $($swpItem.FullName)" -ForegroundColor Cyan
Write-Host ""
Write-Host "How to run in SOLIDWORKS:" -ForegroundColor White
Write-Host "  1. In SOLIDWORKS, go to: Tools -> Macro -> Run..." -ForegroundColor Gray
Write-Host "  2. Select '$($swpItem.Name)' and click Open." -ForegroundColor Gray
Write-Host "  3. (Optional) Add a toolbar button with CAM_DXF_Icon_24.bmp (see README.md)." -ForegroundColor Gray
Write-Host ""
