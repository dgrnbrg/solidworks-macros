<#
.SYNOPSIS
    Builds SolidWorksMcpServer.exe and deploys the unified 'solidworks' Antigravity plugin
    to ~/.gemini/config/plugins/solidworks/, consolidating loose solidworks-addins and
    solidworks-macros standalone skills.
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir

if (-not $SkipBuild) {
    & (Join-Path $scriptDir "Build-McpServer.ps1") -Configuration $Configuration
}

$exePath = Join-Path $repoRoot "src\SolidWorksMcpServer\bin\x64\$Configuration\SolidWorksMcpServer.exe"
if (-not (Test-Path $exePath)) {
    throw "SolidWorksMcpServer.exe not found at $exePath"
}
$exePath = (Resolve-Path $exePath).Path

$srcPluginDir = Join-Path $repoRoot ".agents\plugins\solidworks"
$dstPluginDir = Join-Path $env:USERPROFILE ".gemini\config\plugins\solidworks"

if (-not (Test-Path $dstPluginDir)) {
    New-Item -ItemType Directory -Path $dstPluginDir -Force | Out-Null
}

# Copy files in-place without deleting directories watched by the Antigravity Language Server
Copy-Item -Path (Join-Path $srcPluginDir "*") -Destination $dstPluginDir -Recurse -Force

# Update mcp_config.json with the exact built executable path
$mcpConfigPath = Join-Path $dstPluginDir "mcp_config.json"
$mcpConfig = @{
    mcpServers = @{
        "solidworks-mcp" = @{
            command = $exePath
            args = @()
        }
    }
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($mcpConfigPath, ($mcpConfig | ConvertTo-Json -Depth 5), $utf8NoBom)

# Remove superseded standalone loose skills if they exist in ~/.gemini/config/skills/
$globalSkillsDir = Join-Path $env:USERPROFILE ".gemini\config\skills"
foreach ($legacySkill in @("solidworks-addins", "solidworks-macros")) {
    $legacyPath = Join-Path $globalSkillsDir $legacySkill
    if (Test-Path $legacyPath) {
        Remove-Item -Path $legacyPath -Recurse -Force
        Write-Host "Consolidated & removed superseded loose global skill: $legacyPath" -ForegroundColor Yellow
    }
}

Write-Host "Plugin deployed successfully to: $dstPluginDir" -ForegroundColor Green
Write-Host "MCP server executable: $exePath" -ForegroundColor Green
