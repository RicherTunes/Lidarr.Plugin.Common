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
Write-Host "--- Auth Failure Detection (CRITICAL) ---" -ForegroundColor Yellow

# CRITICAL: 401/403 must emit E2E_AUTH_MISSING, not E2E_LIDARR_UNREACHABLE
Assert-True -Condition ($content -match "httpStatus -eq 401") -Message "Preflight detects HTTP 401 status"
Assert-True -Condition ($content -match "httpStatus -eq 403") -Message "Preflight detects HTTP 403 status"
Assert-True -Condition ($content -match "isAuthFailure") -Message "Preflight has auth failure detection variable"
Assert-True -Condition ($content -match "E2E_AUTH_MISSING") -Message "Preflight emits E2E_AUTH_MISSING on auth failure"
Assert-True -Condition ($content -match "(?s)isAuthFailure.*E2E_AUTH_MISSING") -Message "E2E_AUTH_MISSING is emitted when isAuthFailure is true"
Assert-True -Condition ($content -match "authPatterns") -Message "Preflight checks auth-shaped error messages"

Write-Host ""
Write-Host "--- Retry Logic (IMPORTANT) ---" -ForegroundColor Yellow

# Retry logic with backoff
Assert-True -Condition ($content -match "preflightRetries") -Message "Preflight has retry count variable"
Assert-True -Condition ($content -match "preflightAttempt") -Message "Preflight tracks attempt count"
Assert-True -Condition ($content -match "while.*preflightSuccess.*preflightRetries") -Message "Preflight has retry loop"
Assert-True -Condition ($content -match "backoffSec") -Message "Preflight implements exponential backoff"
Assert-True -Condition ($content -match "Start-Sleep.*backoffSec") -Message "Preflight sleeps between retries"

# Don't retry on auth failures
Assert-True -Condition ($content -match "(?s)401.*break") -Message "Preflight breaks on 401 (no retry)"
Assert-True -Condition ($content -match "(?s)403.*break") -Message "Preflight breaks on 403 (no retry)"

Write-Host ""
Write-Host "--- Env Var Configuration ---" -ForegroundColor Yellow

Assert-True -Condition ($content -match "E2E_PREFLIGHT_TIMEOUT_SEC") -Message "Preflight timeout is configurable via env var"
Assert-True -Condition ($content -match "E2E_PREFLIGHT_RETRIES") -Message "Preflight retries is configurable via env var"
Assert-True -Condition ($content -match "details\['preflightAttempts'\]") -Message "Preflight includes attempt count in details"
Assert-True -Condition ($content -match "details\['preflightRetries'\]") -Message "Preflight includes retry count in details"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
exit 0
