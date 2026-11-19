<#
.SYNOPSIS
    Launches or restarts SOLIDWORKS Connected by signing into the 3DEXPERIENCE Maker
    portal in Microsoft Edge (using saved credentials) and clicking SOLIDWORKS Design
    under Favorite Apps.
#>
[CmdletBinding()]
param(
    [switch]$Restart,
    [switch]$ForceRestart,
    [switch]$InspectOnly,
    [int]$WaitTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir
$exePath = Join-Path $repoRoot "src\SolidWorksMcpServer\bin\x64\Release\SolidWorksMcpServer.exe"

if (-not (Test-Path $exePath)) {
    throw "SolidWorksMcpServer.exe not found at $exePath. Run scripts\Build-McpServer.ps1 first."
}

$cliArgs = @()
if ($InspectOnly) {
    $cliArgs += "--inspect-3dx"
} elseif ($Restart) {
    $cliArgs += "--restart-3dx"
} else {
    $cliArgs += "--launch-3dx"
}

if ($ForceRestart) {
    $cliArgs += "--force"
}
$cliArgs += @("--timeout", $WaitTimeoutSeconds.ToString())

& $exePath @cliArgs
exit $LASTEXITCODE
