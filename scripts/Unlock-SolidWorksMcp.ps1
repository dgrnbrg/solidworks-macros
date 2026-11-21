<#
.SYNOPSIS
    Inspect or force-unlock the exclusive SOLIDWORKS MCP multi-agent session lock.
.DESCRIPTION
    Run with -StatusOnly to inspect which agent currently owns SOLIDWORKS, or run with
    no switches to force-free the lock and terminate any hung SolidWorksMcpServer worker process.
#>
[CmdletBinding()]
param(
    [switch]$StatusOnly,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir
$exePath = Join-Path $repoRoot "src\SolidWorksMcpServer\bin\x64\$Configuration\SolidWorksMcpServer.exe"

if (-not (Test-Path $exePath)) {
    throw "SolidWorksMcpServer.exe not found at: $exePath"
}

$flag = if ($StatusOnly) { "--lock-status" } else { "--force-unlock" }
& $exePath $flag
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
