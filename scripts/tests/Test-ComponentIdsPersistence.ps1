#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unit tests for Write-E2EComponentIdsState structured persistence result.
.DESCRIPTION
    TDD tests for factual persistence outcome: {Attempted, Wrote, Reason}
    - Reason values: lock_timeout, no_changes, written, io_error
    - Attempted = true for any write attempt
    - Wrote = true only when file content actually changed
#>

$ErrorActionPreference = "Stop"
$script:TestsPassed = 0
$script:TestsFailed = 0

function Write-TestResult {
    param(
        [string]$TestName,
        [bool]$Passed,
        [string]$Message = ""
    )

    if ($Passed) {
        Write-Host "  [PASS] $TestName" -ForegroundColor Green
        $script:TestsPassed++
    }
    else {
        Write-Host "  [FAIL] $TestName" -ForegroundColor Red
        if ($Message) {
            Write-Host "         $Message" -ForegroundColor Yellow
        }
        $script:TestsFailed++
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Component ID Persistence Outcome Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$modulePath = Join-Path (Join-Path $PSScriptRoot "..") "lib/e2e-component-ids.psm1"
Import-Module $modulePath -Force

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("e2e-persistence-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    # ==========================================================================
    # Test: lock_timeout returns structured result
    # ==========================================================================
    Write-Host "`nlock_timeout scenario:" -ForegroundColor Yellow

    $statePath = Join-Path $tempRoot "lock_timeout.json"
    $state = @{ schemaVersion = 2; instances = @{} }

    # Create a lock file to simulate contention
    Set-Content -LiteralPath "$statePath.lock" -Value "locked" -Encoding UTF8 -NoNewline

    # Override timeout to make test fast
    $oldTimeout = $env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS
    $oldStale = $env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS
    try {
        $env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS = "1"
        $env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS = "9999"  # Don't treat as stale

        $result = Write-E2EComponentIdsState -Path $statePath -State $state

        Write-TestResult -TestName "lock_timeout: returns hashtable" -Passed ($result -is [hashtable])
        Write-TestResult -TestName "lock_timeout: Attempted = true" -Passed ($result.Attempted -eq $true)
        Write-TestResult -TestName "lock_timeout: Wrote = false" -Passed ($result.Wrote -eq $false)
        Write-TestResult -TestName "lock_timeout: Reason = 'lock_timeout'" -Passed ($result.Reason -eq "lock_timeout")
    }
    finally {
        if ($null -eq $oldTimeout) { Remove-Item env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS -ErrorAction SilentlyContinue }
        else { $env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS = $oldTimeout }
        if ($null -eq $oldStale) { Remove-Item env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS -ErrorAction SilentlyContinue }
        else { $env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS = $oldStale }
        Remove-Item -LiteralPath "$statePath.lock" -Force -ErrorAction SilentlyContinue
    }

    # ==========================================================================
    # Test: no_changes returns structured result (file already has same content)
    # ==========================================================================
    Write-Host "`nno_changes scenario:" -ForegroundColor Yellow

    $statePath = Join-Path $tempRoot "no_changes.json"
    $state = [ordered]@{
        schemaVersion = 2
        instances = [ordered]@{
            "test-instance" = [ordered]@{
                plugins = [ordered]@{
                    "Qobuzarr" = [ordered]@{ indexerId = 101 }
                }
            }
        }
    }

    # Write initial state
    $initialResult = Write-E2EComponentIdsState -Path $statePath -State $state
    Write-TestResult -TestName "no_changes: initial write succeeds" -Passed ($initialResult.Wrote -eq $true)

    # Write same state again - should detect no_changes
    $result = Write-E2EComponentIdsState -Path $statePath -State $state

    Write-TestResult -TestName "no_changes: returns hashtable" -Passed ($result -is [hashtable])
    Write-TestResult -TestName "no_changes: Attempted = true" -Passed ($result.Attempted -eq $true)
    Write-TestResult -TestName "no_changes: Wrote = false" -Passed ($result.Wrote -eq $false)
    Write-TestResult -TestName "no_changes: Reason = 'no_changes'" -Passed ($result.Reason -eq "no_changes")

    # ==========================================================================
    # Test: written returns structured result (file content changed)
    # ==========================================================================
    Write-Host "`nwritten scenario:" -ForegroundColor Yellow

    $statePath = Join-Path $tempRoot "written.json"
    $state = @{ schemaVersion = 2; instances = @{} }

    $result = Write-E2EComponentIdsState -Path $statePath -State $state

    Write-TestResult -TestName "written: returns hashtable" -Passed ($result -is [hashtable])
    Write-TestResult -TestName "written: Attempted = true" -Passed ($result.Attempted -eq $true)
    Write-TestResult -TestName "written: Wrote = true" -Passed ($result.Wrote -eq $true)
    Write-TestResult -TestName "written: Reason = 'written'" -Passed ($result.Reason -eq "written")

    # Also test update scenario
    $state2 = @{
        schemaVersion = 2
        instances = @{
            "new-instance" = @{ plugins = @{ "Qobuzarr" = @{ indexerId = 999 } } }
        }
    }
    $result2 = Write-E2EComponentIdsState -Path $statePath -State $state2
    Write-TestResult -TestName "written: update returns Wrote = true" -Passed ($result2.Wrote -eq $true)
    Write-TestResult -TestName "written: update returns Reason = 'written'" -Passed ($result2.Reason -eq "written")

    # ==========================================================================
    # Test: io_error returns structured result (write failure)
    # ==========================================================================
    Write-Host "`nio_error scenario:" -ForegroundColor Yellow

    # Create a directory with same name as target file to cause IO error
    $statePath = Join-Path $tempRoot "io_error.json"
    $tmpPath = "$statePath.tmp"
    New-Item -ItemType Directory -Path $tmpPath -Force | Out-Null

    $state = @{ schemaVersion = 2; instances = @{} }
    $result = Write-E2EComponentIdsState -Path $statePath -State $state

    Write-TestResult -TestName "io_error: returns hashtable" -Passed ($result -is [hashtable])
    Write-TestResult -TestName "io_error: Attempted = true" -Passed ($result.Attempted -eq $true)
    Write-TestResult -TestName "io_error: Wrote = false" -Passed ($result.Wrote -eq $false)
    Write-TestResult -TestName "io_error: Reason = 'io_error'" -Passed ($result.Reason -eq "io_error")

    Remove-Item -LiteralPath $tmpPath -Recurse -Force -ErrorAction SilentlyContinue

    # ==========================================================================
    # Test: invariant - Attempted is always true when function returns
    # ==========================================================================
    Write-Host "`nInvariants:" -ForegroundColor Yellow

    $statePath = Join-Path $tempRoot "invariant.json"
    $state = @{ schemaVersion = 2; instances = @{} }

    $result = Write-E2EComponentIdsState -Path $statePath -State $state
    Write-TestResult -TestName "invariant: Attempted is always present" -Passed ($null -ne $result.Attempted)
    Write-TestResult -TestName "invariant: Wrote is always present" -Passed ($null -ne $result.Wrote)
    Write-TestResult -TestName "invariant: Reason is always present" -Passed (-not [string]::IsNullOrEmpty($result.Reason))
    Write-TestResult -TestName "invariant: Reason is valid enum" -Passed ($result.Reason -in @("written", "no_changes", "lock_timeout", "io_error"))

    # ==========================================================================
    # Test: backward compatibility - boolean coercion still works
    # ==========================================================================
    Write-Host "`nBackward compatibility:" -ForegroundColor Yellow

    $statePath = Join-Path $tempRoot "compat.json"
    $state = @{ schemaVersion = 2; instances = @{} }

    $result = Write-E2EComponentIdsState -Path $statePath -State $state

    # Ensure boolean coercion works for legacy callers that do: if ($result) { ... }
    $boolValue = [bool]$result.Wrote
    Write-TestResult -TestName "backward compat: Wrote can be coerced to bool" -Passed ($boolValue -eq $true)

    # Lock timeout case
    Set-Content -LiteralPath "$statePath.lock" -Value "locked" -Encoding UTF8 -NoNewline
    $oldTimeout = $env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS
    $oldStale = $env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS
    try {
        $env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS = "1"
        $env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS = "9999"
        $result = Write-E2EComponentIdsState -Path $statePath -State $state
        $boolValue = [bool]$result.Wrote
        Write-TestResult -TestName "backward compat: lock_timeout Wrote coerces to false" -Passed ($boolValue -eq $false)
    }
    finally {
        if ($null -eq $oldTimeout) { Remove-Item env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS -ErrorAction SilentlyContinue }
        else { $env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS = $oldTimeout }
        if ($null -eq $oldStale) { Remove-Item env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS -ErrorAction SilentlyContinue }
        else { $env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS = $oldStale }
        Remove-Item -LiteralPath "$statePath.lock" -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results: $script:TestsPassed passed, $script:TestsFailed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($script:TestsFailed -gt 0) { exit 1 }
