#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Regression tests for Lidarr API preflight behavior in e2e-runner.ps1.
.DESCRIPTION
    Ensures the runner emits E2E_LIDARR_UNREACHABLE explicitly at the source
    (before per-plugin gates) when Lidarr API is not reachable.
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$passed = 0
$failed = 0

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )
    if ($Condition) {
        Write-Host "  [PASS] $Message" -ForegroundColor Green
        $script:passed++
    }
    else {
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        $script:failed++
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Runner Lidarr API Preflight Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$runnerPath = Join-Path (Join-Path $PSScriptRoot "..") "e2e-runner.ps1"
$content = Get-Content -LiteralPath $runnerPath -Raw

Assert-True -Condition ($content -match "E2E_LIDARR_UNREACHABLE") -Message "Runner references E2E_LIDARR_UNREACHABLE"
Assert-True -Condition ($content -match "(?s)Invoke-LidarrApi\s+-Endpoint\s+'system/status'") -Message "Runner performs Lidarr API preflight against system/status"
Assert-True -Condition ($content -match "(?s)New-OutcomeResult\s+-Gate\s+'LidarrApi'.*-PluginName\s+'Ecosystem'") -Message "Runner emits a LidarrApi/Ecosystem result on preflight failure"
Assert-True -Condition ($content -match "/api/v1/system/status") -Message "Preflight details include endpoint path-only (/api/v1/system/status)"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
exit 0
