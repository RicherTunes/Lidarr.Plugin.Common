#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Quick test script for Tidalarr with persistent Lidarr config.

.DESCRIPTION
    Runs Lidarr with the Tidalarr plugin, preserving config between runs.
    First run: Complete the OAuth PKCE flow and paste RedirectUrl in the UI.
    Subsequent runs: Config is preserved; rebuild/redeploy the plugin as needed.

.PARAMETER Rebuild
    Rebuild the plugin before starting.

.PARAMETER Clean
    Delete persistent config and start fresh.

.PARAMETER LidarrTag
    Lidarr Docker tag. Default: pr-plugins-3.1.1.4884

.PARAMETER Port
    Host port to bind Lidarr to. Default: 8690

.PARAMETER ContainerName
    Docker container name. Default: tidalarr-test
#>
param(
    [switch]$Rebuild,
    [switch]$Clean,
    [string]$LidarrTag = "pr-plugins-3.1.1.4884",
    [int]$Port = 8690,
    [string]$ContainerName = "tidalarr-test"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$commonRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $commonRoot
$tidalarrRoot = Join-Path $repoRoot "tidalarr"

if (-not (Test-Path (Join-Path $tidalarrRoot "Tidalarr.sln"))) {
    throw "Tidalarr repo not found at '$tidalarrRoot'."
}

function Find-LatestZip {
    param([Parameter(Mandatory = $true)][string]$Directory)

    if (-not (Test-Path $Directory)) { return $null }
    return (Get-ChildItem -LiteralPath $Directory -Filter *.zip -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1)
}

$persistentRoot = Join-Path $commonRoot ".persistent-tidalarr-test-config"
$configDir = Join-Path $persistentRoot "config"
$pluginsDir = Join-Path $persistentRoot "plugins/RicherTunes/Tidalarr"

if ($Clean) {
    Write-Host "Cleaning persistent config..." -ForegroundColor Yellow
    Remove-Item -Path $persistentRoot -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $configDir -Force | Out-Null
New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null

if ($Rebuild) {
    Write-Host "Building Tidalarr package..." -ForegroundColor Cyan
    Push-Location $tidalarrRoot
    try {
        & pwsh -File ".\\build.ps1" -Package -Configuration Release | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Tidalarr build.ps1 -Package failed." }
    }
    finally {
        Pop-Location
    }
}

$zip = Find-LatestZip -Directory (Join-Path $tidalarrRoot "src/Tidalarr/artifacts/packages")
if (-not $zip) {
    throw "No Tidalarr package zip found under 'tidalarr/src/Tidalarr/artifacts/packages'. Run with -Rebuild."
}

Write-Host "Tidalarr zip: $($zip.FullName)" -ForegroundColor Gray

Write-Host "Extracting plugin to persistent directory..." -ForegroundColor Cyan
Remove-Item -Path (Join-Path $pluginsDir "*") -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $zip.FullName -DestinationPath $pluginsDir -Force

docker stop $ContainerName 2>$null | Out-Null
docker rm $ContainerName 2>$null | Out-Null

$configMount = $configDir.Replace('\', '/')
$pluginsMount = (Split-Path $pluginsDir -Parent).Replace('\', '/')

Write-Host "Starting Lidarr with persistent config..." -ForegroundColor Cyan
docker run -d `
    --name $ContainerName `
    -p "${Port}:8686" `
    -v "${configMount}:/config" `
    -v "${pluginsMount}:/config/plugins/RicherTunes" `
    -e PUID=1000 `
    -e PGID=1000 `
    -e TZ=UTC `
    "ghcr.io/hotio/lidarr:$LidarrTag" | Out-Null

Write-Host "Waiting for Lidarr to start..." -ForegroundColor Yellow
$timeoutSeconds = 60
$start = Get-Date
$ready = $false
while (((Get-Date) - $start).TotalSeconds -lt $timeoutSeconds) {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$Port/api/v1/system/status" -TimeoutSec 5 -ErrorAction SilentlyContinue
        if ($response.StatusCode -eq 200) {
            $ready = $true
            break
        }
    }
    catch { }
    Start-Sleep -Seconds 2
}

if (-not $ready) {
    Write-Warning "Timed out waiting for Lidarr to respond on http://localhost:$Port. Container may still be starting."
    try {
        Write-Host "Recent container logs:" -ForegroundColor Yellow
        docker logs $ContainerName --tail 200 | Out-Host
    }
    catch { }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Lidarr is running at: http://localhost:$Port" -ForegroundColor Green
Write-Host "Config persisted at: $configDir" -ForegroundColor Green
Write-Host "Plugin persisted at: $pluginsDir" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Tidalarr OAuth setup (first run):" -ForegroundColor Yellow
Write-Host "1) Add Tidalarr indexer/download client in Lidarr UI" -ForegroundColor Yellow
Write-Host "2) Set ConfigPath to: /config/tidalarr" -ForegroundColor Yellow
Write-Host "3) Click to generate the auth URL, login, then paste the RedirectUrl back" -ForegroundColor Yellow
Write-Host "4) Set TidalMarket (e.g., US) and click Test" -ForegroundColor Yellow
Write-Host ""
Write-Host "To stop: docker stop $ContainerName" -ForegroundColor Gray
