#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Quick test script for Qobuzarr with persistent Lidarr config.

.DESCRIPTION
    Runs Lidarr with Qobuzarr plugin, preserving config between runs.
    First run: Configure credentials in UI at http://localhost:8689
    Subsequent runs: Config is preserved, just test.

.PARAMETER Rebuild
    Rebuild the plugin before starting.

.PARAMETER Clean
    Delete persistent config and start fresh.
#>
param(
    [switch]$Rebuild,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$qobuzarrRoot = Join-Path $repoRoot "qobuzarr"
$commonRoot = Join-Path $repoRoot "lidarr.plugin.common"

# Persistent config directory
$persistentConfig = Join-Path $commonRoot ".persistent-test-config"
$configDir = Join-Path $persistentConfig "config"
$pluginsDir = Join-Path $persistentConfig "plugins/RicherTunes/Qobuzarr"

$containerName = "qobuzarr-test"
$port = 8689

if ($Clean) {
    Write-Host "Cleaning persistent config..." -ForegroundColor Yellow
    Remove-Item -Path $persistentConfig -Recurse -Force -ErrorAction SilentlyContinue
}

# Create directories
New-Item -ItemType Directory -Path $configDir -Force | Out-Null
New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null

# Build plugin if requested or if it doesn't exist
$zipPath = Join-Path $qobuzarrRoot "artifacts/packages/qobuzarr-0.1.0-dev-net8.0.zip"
if ($Rebuild -or -not (Test-Path $zipPath)) {
    Write-Host "Building Qobuzarr plugin..." -ForegroundColor Cyan
    Push-Location $qobuzarrRoot
    try {
        Import-Module (Join-Path $commonRoot "tools/PluginPack.psm1") -Force
        $zipPath = New-PluginPackage -Csproj "Qobuzarr.csproj" -Manifest "plugin.json" -MergeAssemblies -Framework "net8.0"
    }
    finally {
        Pop-Location
    }
}

# Extract plugin
Write-Host "Extracting plugin to persistent directory..." -ForegroundColor Cyan
Remove-Item -Path (Join-Path $pluginsDir "*") -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $zipPath -DestinationPath $pluginsDir -Force

# Stop existing container
docker stop $containerName 2>$null | Out-Null
docker rm $containerName 2>$null | Out-Null

# Start container with persistent config
$configMount = $configDir.Replace('\', '/')
$pluginsMount = (Split-Path $pluginsDir -Parent).Replace('\', '/')

Write-Host "Starting Lidarr with persistent config..." -ForegroundColor Cyan
docker run -d `
    --name $containerName `
    -p "${port}:8686" `
    -v "${configMount}:/config" `
    -v "${pluginsMount}:/config/plugins/RicherTunes" `
    -e PUID=1000 `
    -e PGID=1000 `
    -e TZ=UTC `
    "ghcr.io/hotio/lidarr:pr-plugins-3.1.1.4884"

# Wait for startup
Write-Host "Waiting for Lidarr to start..." -ForegroundColor Yellow
$timeout = 60
$start = Get-Date
while (((Get-Date) - $start).TotalSeconds -lt $timeout) {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$port/api/v1/system/status" -TimeoutSec 5 -ErrorAction SilentlyContinue
        if ($response.StatusCode -eq 200) {
            break
        }
    }
    catch { }
    Start-Sleep -Seconds 2
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Lidarr is running at: http://localhost:$port" -ForegroundColor Green
Write-Host "Config persisted at: $configDir" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "First run: Configure Qobuz credentials in UI" -ForegroundColor Yellow
Write-Host "Subsequent runs: Config is preserved!" -ForegroundColor Yellow
Write-Host ""
Write-Host "To stop: docker stop $containerName" -ForegroundColor Cyan
Write-Host "To view logs: docker logs -f $containerName" -ForegroundColor Cyan
