#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Persistent local Lidarr Docker runner for Qobuzarr + Tidalarr.

.DESCRIPTION
    Builds (optional), stages, and runs Lidarr with both Qobuzarr and Tidalarr plugins
    while persisting /config and plugin installation between runs.

    This is intended for iterative UI configuration (credentials, settings) and
    repeatable validation without redoing setup each time.

.PARAMETER Rebuild
    Rebuild both plugins before starting.

.PARAMETER RebuildQobuzarr
    Rebuild Qobuzarr before starting.

.PARAMETER RebuildTidalarr
    Rebuild Tidalarr before starting.

.PARAMETER Clean
    Delete persistent state (config, plugins, downloads) and start fresh.

.PARAMETER LidarrTag
    Lidarr Docker tag. Default: pr-plugins-3.1.1.4884

.PARAMETER Port
    Host port to bind Lidarr to. Default: 8691

.PARAMETER ContainerName
    Docker container name. Default: lidarr-multi-plugin-persist

.PARAMETER WorkRoot
    Directory (relative to lidarr.plugin.common repo root) to persist state under.
    Default: .persistent-multi

.PARAMETER KeepRunning
    Keep container running after the smoke test completes.
#>
param(
    [switch]$Rebuild,
    [switch]$RebuildQobuzarr,
    [switch]$RebuildTidalarr,
    [switch]$Clean,
    [string]$LidarrTag = "pr-plugins-3.1.1.4884",
    [int]$Port = 8691,
    [string]$ContainerName = "lidarr-multi-plugin-persist",
    [string]$WorkRoot = ".persistent-multi",
    [switch]$KeepRunning,
    [switch]$RunSearchGate,
    [switch]$RunGrabGate,
    [int]$SearchTimeoutSeconds = 180,
    [string]$SearchArtistTerm = "Miles Davis",
    [string]$SearchAlbumTitle = "Kind of Blue"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$commonRoot = Join-Path $repoRoot "lidarr.plugin.common"
$qobuzarrRoot = Join-Path $repoRoot "qobuzarr"
$tidalarrRoot = Join-Path $repoRoot "tidalarr"

if (-not (Test-Path (Join-Path $qobuzarrRoot "Qobuzarr.csproj"))) {
    throw "Qobuzarr repo not found at '$qobuzarrRoot'."
}
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

if ($Rebuild) {
    $RebuildQobuzarr = $true
    $RebuildTidalarr = $true
}

if ($RebuildQobuzarr) {
    Write-Host "Building Qobuzarr package..." -ForegroundColor Cyan
    Push-Location $qobuzarrRoot
    try {
        Import-Module (Join-Path $commonRoot "tools/PluginPack.psm1") -Force
        $null = New-PluginPackage -Csproj "Qobuzarr.csproj" -Manifest "plugin.json" -MergeAssemblies -Framework "net8.0" -Configuration Release
    }
    finally {
        Pop-Location
    }
}

if ($RebuildTidalarr) {
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

$qZip = Find-LatestZip -Directory (Join-Path $qobuzarrRoot "artifacts/packages")
if (-not $qZip) { throw "No Qobuzarr package zip found under 'qobuzarr/artifacts/packages'." }

$tZip = Find-LatestZip -Directory (Join-Path $tidalarrRoot "src/Tidalarr/artifacts/packages")
if (-not $tZip) { throw "No Tidalarr package zip found under 'tidalarr/src/Tidalarr/artifacts/packages'." }

$hostOverride = $null
$upstreamHost = Join-Path $repoRoot "_upstream/Lidarr/_output/net8.0/Lidarr.Common.dll"
if (Test-Path $upstreamHost) {
    $hostOverride = (Resolve-Path $upstreamHost).Path
}

Write-Host "Qobuzarr zip: $($qZip.FullName)" -ForegroundColor Gray
Write-Host "Tidalarr zip: $($tZip.FullName)" -ForegroundColor Gray
if ($hostOverride) {
    Write-Host "Host override: $hostOverride" -ForegroundColor Yellow
}
else {
    Write-Host "Host override: (none) - multi-plugin load may depend on upstream host fixes." -ForegroundColor Yellow
}

$smokeScript = Join-Path $commonRoot "scripts/multi-plugin-docker-smoke-test.ps1"
$params = @{
    PluginZip = @("qobuzarr=$($qZip.FullName)", "tidalarr=$($tZip.FullName)")
    LidarrTag = $LidarrTag
    ContainerName = $ContainerName
    Port = $Port
    WorkRoot = $WorkRoot
    PreserveState = $true
}
if ($Clean) { $params.CleanState = $true }
if ($KeepRunning) { $params.KeepRunning = $true }
if ($hostOverride) { $params.HostOverrideAssembly = @($hostOverride) }
if ($RunSearchGate) {
    $params.RunSearchGate = $true
    $params.UseExistingConfigForSearchGate = $true
    $params.SearchTimeoutSeconds = $SearchTimeoutSeconds
    $params.SearchArtistTerm = $SearchArtistTerm
    $params.SearchAlbumTitle = $SearchAlbumTitle
}
if ($RunGrabGate) {
    $params.RunGrabGate = $true
    $params.UseExistingConfigForSearchGate = $true
    $params.UseExistingConfigForDownloadClientGate = $true
    $params.UseExistingConfigForGrabGate = $true
    $params.SearchTimeoutSeconds = $SearchTimeoutSeconds
    $params.SearchArtistTerm = $SearchArtistTerm
    $params.SearchAlbumTitle = $SearchAlbumTitle
}

Push-Location $commonRoot
try {
    & $smokeScript @params | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

Write-Host "" 
Write-Host "Lidarr UI: http://localhost:$Port" -ForegroundColor Green
Write-Host "Persisted state: $(Join-Path $commonRoot (Join-Path $WorkRoot $ContainerName))" -ForegroundColor Green
