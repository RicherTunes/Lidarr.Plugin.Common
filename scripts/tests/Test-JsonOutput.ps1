#!/usr/bin/env pwsh
<#
.SYNOPSIS
    TDD tests for e2e-runner.ps1 --json output functionality.
.DESCRIPTION
    Validates JSON output schema, outcome values, and secret redaction.
    Schema version: 1.1
    Schema ID: richer-tunes.lidarr.e2e-run-manifest
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Import the JSON output module
$scriptRoot = $PSScriptRoot
$libPath = Join-Path (Join-Path $scriptRoot "..") "lib"
$jsonModulePath = Join-Path $libPath "e2e-json-output.psm1"

# Test results tracking
$script:testsPassed = 0
$script:testsFailed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "  [PASS] $Message" -ForegroundColor Green
        $script:testsPassed++
    } else {
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        $script:testsFailed++
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -eq $Actual) {
        Write-Host "  [PASS] $Message" -ForegroundColor Green
        $script:testsPassed++
    } else {
        Write-Host "  [FAIL] $Message (expected: $Expected, got: $Actual)" -ForegroundColor Red
        $script:testsFailed++
    }
}

# ============================================================================
# Test Data Fixtures
# ============================================================================

function New-MockGateResult {
    param(
        [string]$Gate = "Schema",
        [string]$PluginName = "Qobuzarr",
        [string]$Outcome = "success",
        [string[]]$Errors = @(),
        [hashtable]$Details = @{},
        [string]$SkipReason = $null
    )
    $result = [PSCustomObject]@{
        Gate = $Gate
        PluginName = $PluginName
        Outcome = $Outcome
        Success = ($Outcome -eq 'success')
        Errors = $Errors
        Details = $Details
        StartTime = [DateTime]::UtcNow.AddSeconds(-2)
        EndTime = [DateTime]::UtcNow
    }
    if ($SkipReason) {
        $result.Details['SkipReason'] = $SkipReason
    }
    return $result
}

function New-MockRunContext {
    param(
        [string]$LidarrUrl = "http://localhost:8686",
        [string]$ContainerName = "lidarr-e2e-test",
        [string]$ImageTag = "pr-plugins-3.1.1.4884",
        [string]$RequestedGate = "bootstrap",
        [string[]]$Plugins = @("Qobuzarr", "Tidalarr"),
        [string[]]$EffectiveGates = @("Schema", "Configure", "Search"),
        [bool]$RedactionPassed = $true,
        [string]$LidarrVersion = "2.9.6.4552",
        [string]$LidarrBranch = "plugins"
    )
    return @{
        LidarrUrl = $LidarrUrl
        ContainerName = $ContainerName
        ImageTag = $ImageTag
        ImageDigest = "sha256:abc123def456"
        RequestedGate = $RequestedGate
        Plugins = $Plugins
        EffectiveGates = $EffectiveGates
        EffectivePlugins = $Plugins
        RedactionSelfTestExecuted = $true
        RedactionSelfTestPassed = $RedactionPassed
        RunnerArgs = @("-Gate", "bootstrap", "-Plugins", "Qobuzarr,Tidalarr")
        DiagnosticsBundlePath = $null
        LidarrVersion = $LidarrVersion
        LidarrBranch = $LidarrBranch
    }
}

# ============================================================================
# Schema Tests - Core Structure
# ============================================================================

$schemaTests = @(
    @{
        Name = "schemaVersion is '1.1'"
        Test = { param($obj) $obj.schemaVersion -eq "1.1" }
    }
    @{
        Name = "schemaId is 'richer-tunes.lidarr.e2e-run-manifest'"
        Test = { param($obj) $obj.schemaId -eq "richer-tunes.lidarr.e2e-run-manifest" }
    }
    @{
        Name = "timestamp is ISO 8601 parseable"
        Test = {
            param($obj)
            try { [DateTime]::Parse($obj.timestamp) | Out-Null; $true }
            catch { $false }
        }
    }
    @{
        Name = "runId is present and non-empty"
        Test = { param($obj) -not [string]::IsNullOrWhiteSpace($obj.runId) }
    }
    @{
        Name = "runner.name contains 'e2e-runner'"
        Test = { param($obj) $obj.runner.name -match 'e2e-runner' }
    }
    @{
        Name = "runner.version is present"
        Test = { param($obj) -not [string]::IsNullOrWhiteSpace($obj.runner.version) }
    }
)

# ============================================================================
# Schema Tests - Request vs Effective (forward-compat #2)
# ============================================================================

$requestEffectiveTests = @(
    @{
        Name = "request.gate contains requested gate"
        Test = { param($obj) $null -ne $obj.request.gate }
    }
    @{
        Name = "request.plugins is an array"
        Test = { param($obj) $obj.request.plugins -is [array] }
    }
    @{
        Name = "effective.gates is an array showing expanded gates"
        Test = { param($obj) $obj.effective.gates -is [array] }
    }
    @{
        Name = "effective.plugins is an array"
        Test = { param($obj) $obj.effective.plugins -is [array] }
    }
    @{
        Name = "effective.gates contains more gates than 'bootstrap' when bootstrap requested"
        Test = {
            param($obj)
            if ($obj.request.gate -eq 'bootstrap') {
                return $obj.effective.gates.Count -gt 1
            }
            return $true
        }
    }
)

# ============================================================================
# Schema Tests - Host Fingerprinting (forward-compat #5)
# ============================================================================

$hostFingerprintTests = @(
    @{
        Name = "lidarr.url is present"
        Test = { param($obj) -not [string]::IsNullOrWhiteSpace($obj.lidarr.url) }
    }
    @{
        Name = "lidarr.containerName is present"
        Test = { param($obj) -not [string]::IsNullOrWhiteSpace($obj.lidarr.containerName) }
    }
    @{
        Name = "lidarr.imageTag is present when available"
        Test = { param($obj) $obj.lidarr.PSObject.Properties.Name -contains 'imageTag' }
    }
    @{
        Name = "lidarr.version is present when queryable"
        Test = { param($obj) $obj.lidarr.PSObject.Properties.Name -contains 'version' }
    }
    @{
        Name = "lidarr.imageDigest is present when available"
        Test = { param($obj) $obj.lidarr.PSObject.Properties.Name -contains 'imageDigest' }
    }
)

# ============================================================================
# Schema Tests - Results Structure
# ============================================================================

$resultsTests = @(
    @{
        Name = "results is an array"
        Test = { param($obj) $obj.results -is [array] }
    }
    @{
        Name = "each result has gate (lowerCamelCase not required for gate name)"
        Test = {
            param($obj)
            foreach ($r in $obj.results) { if ($null -eq $r.gate) { return $false } }
            return $true
        }
    }
    @{
        Name = "each result has plugin"
        Test = {
            param($obj)
            foreach ($r in $obj.results) { if ($null -eq $r.plugin) { return $false } }
            return $true
        }
    }
    @{
        Name = "each result has outcome in (success, failed, skipped)"
        Test = {
            param($obj)
            $valid = @('success', 'failed', 'skipped')
            foreach ($r in $obj.results) {
                if ($r.outcome -notin $valid) { return $false }
            }
            return $true
        }
    }
    @{
        Name = "each result has durationMs >= 0"
        Test = {
            param($obj)
            foreach ($r in $obj.results) {
                if ($null -eq $r.durationMs -or $r.durationMs -lt 0) { return $false }
            }
            return $true
        }
    }
    @{
        Name = "each result has errors array"
        Test = {
            param($obj)
            foreach ($r in $obj.results) {
                if ($r.errors -isnot [array]) { return $false }
            }
            return $true
        }
    }
    @{
        Name = "each result has details object"
        Test = {
            param($obj)
            foreach ($r in $obj.results) {
                if ($null -eq $r.details) { return $false }
            }
            return $true
        }
    }
)

# ============================================================================
# Schema Tests - Outcome Reason (forward-compat #4)
# ============================================================================

$outcomeReasonTests = @(
    @{
        Name = "skipped results have outcomeReason"
        Test = {
            param($obj)
            foreach ($r in $obj.results) {
                if ($r.outcome -eq 'skipped' -and [string]::IsNullOrWhiteSpace($r.outcomeReason)) {
                    return $false
                }
            }
            return $true
        }
    }
    @{
        Name = "outcomeReason does not contain secret values"
        Test = {
            param($obj)
            foreach ($r in $obj.results) {
                if ($r.outcomeReason) {
                    # Should say "Missing: authToken" not "authToken=abc123"
                    if ($r.outcomeReason -match '=[a-zA-Z0-9]{10,}') { return $false }
                }
            }
            return $true
        }
    }
)

# ============================================================================
# Schema Tests - Summary
# ============================================================================

$summaryTests = @(
    @{
        Name = "summary.overallSuccess is boolean"
        Test = { param($obj) $obj.summary.overallSuccess -is [bool] }
    }
    @{
        Name = "summary.totalGates matches results count"
        Test = { param($obj) $obj.summary.totalGates -eq $obj.results.Count }
    }
    @{
        Name = "summary.passed + failed + skipped = totalGates"
        Test = {
            param($obj)
            $sum = $obj.summary.passed + $obj.summary.failed + $obj.summary.skipped
            return $sum -eq $obj.summary.totalGates
        }
    }
    @{
        Name = "summary.totalDurationMs is present"
        Test = { param($obj) $null -ne $obj.summary.totalDurationMs }
    }
)

# ============================================================================
# Schema Tests - Diagnostics (forward-compat #6)
# ============================================================================

$diagnosticsTests = @(
    @{
        Name = "diagnostics.bundleCreated is boolean"
        Test = { param($obj) $obj.diagnostics.bundleCreated -is [bool] }
    }
    @{
        Name = "diagnostics.bundlePath is present (null or string)"
        Test = { param($obj) $obj.diagnostics.PSObject.Properties.Name -contains 'bundlePath' }
    }
    @{
        Name = "diagnostics.redactionApplied is boolean"
        Test = { param($obj) $obj.diagnostics.redactionApplied -is [bool] }
    }
    @{
        Name = "diagnostics.redactionSelfTestPassed is boolean"
        Test = { param($obj) $obj.diagnostics.redactionSelfTestPassed -is [bool] }
    }
)

# ============================================================================
# Schema Tests - Redaction (forward-compat #3 + secrets)
# ============================================================================

$redactionTests = @(
    @{
        Name = "redaction.selfTestExecuted is boolean"
        Test = { param($obj) $obj.redaction.selfTestExecuted -is [bool] }
    }
    @{
        Name = "redaction.selfTestPassed is boolean"
        Test = { param($obj) $obj.redaction.selfTestPassed -is [bool] }
    }
    @{
        Name = "redaction.patternsCount is integer"
        Test = { param($obj) $obj.redaction.patternsCount -is [int] -or $obj.redaction.patternsCount -is [long] }
    }
)

# ============================================================================
# Secret Redaction Tests
# ============================================================================

$secretTests = @(
    @{
        Name = "runner.args does not contain API key patterns"
        Test = {
            param($json)
            # 32-char hex strings look like API keys
            return $json -notmatch '"[a-f0-9]{32}"'
        }
    }
    @{
        Name = "lidarr.url has private IPs redacted"
        Test = {
            param($json)
            # Private IPs should be [PRIVATE-IP] or [LOCALHOST]
            return $json -notmatch '"url"\s*:\s*"[^"]*192\.168\.' -and
                   $json -notmatch '"url"\s*:\s*"[^"]*10\.\d+\.\d+\.\d+'
        }
    }
    @{
        Name = "details do not contain authToken values"
        Test = {
            param($json)
            return $json -notmatch '"authToken"\s*:\s*"[^"\[\]]{8,}"'
        }
    }
    @{
        Name = "details do not contain password values"
        Test = {
            param($json)
            return $json -notmatch '"password"\s*:\s*"[^"\[\]]{4,}"'
        }
    }
    @{
        Name = "credential fields show names only, not values"
        Test = {
            param($json)
            $obj = $json | ConvertFrom-Json
            foreach ($r in $obj.results) {
                $creds = $r.details.PSObject.Properties | Where-Object { $_.Name -eq 'credentialAllOf' }
                if ($creds -and $creds.Value) {
                    foreach ($f in $creds.Value) {
                        if ($f.Length -gt 50) { return $false }
                    }
                }
            }
            return $true
        }
    }
)

# ============================================================================
# LowerCamelCase Tests (forward-compat #3)
# ============================================================================

$camelCaseTests = @(
    @{
        Name = "details keys are lowerCamelCase (indexerFound not IndexerFound)"
        Test = {
            param($obj)
            foreach ($r in $obj.results) {
                foreach ($key in $r.details.PSObject.Properties.Name) {
                    # First char should be lowercase (lowerCamelCase)
                    if ($key.Length -gt 0 -and $key[0] -cmatch '[A-Z]') {
                        Write-Host "    Warning: details key '$key' is not lowerCamelCase" -ForegroundColor Yellow
                        return $false
                    }
                }
            }
            return $true
        }
    }
)

# ============================================================================
# Run Tests
# ============================================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "JSON Output Schema Tests (v1.1)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if (Test-Path $jsonModulePath) {
    Import-Module $jsonModulePath -Force

    # Create mock data with various outcomes
    $mockResults = @(
        (New-MockGateResult -Gate "Schema" -PluginName "Qobuzarr" -Outcome "success" -Details @{
            IndexerFound = $true
            DownloadClientFound = $true
        })
        (New-MockGateResult -Gate "Configure" -PluginName "Qobuzarr" -Outcome "success" -Details @{
            Actions = @("Created indexer", "Created download client")
        })
        (New-MockGateResult -Gate "Search" -PluginName "Qobuzarr" -Outcome "skipped" -SkipReason "Missing env vars: QOBUZARR_AUTH_TOKEN" -Details @{
            CredentialAllOf = @("authToken", "userId")
        })
    )

    $mockContext = New-MockRunContext

    # Generate JSON
    $json = ConvertTo-E2ERunManifest -Results $mockResults -Context $mockContext
    $obj = $json | ConvertFrom-Json

    # Run all test groups
    $testGroups = @(
        @{ Name = "Core Schema"; Tests = $schemaTests }
        @{ Name = "Request vs Effective"; Tests = $requestEffectiveTests }
        @{ Name = "Host Fingerprinting"; Tests = $hostFingerprintTests }
        @{ Name = "Results Structure"; Tests = $resultsTests }
        @{ Name = "Outcome Reason"; Tests = $outcomeReasonTests }
        @{ Name = "Summary"; Tests = $summaryTests }
        @{ Name = "Diagnostics"; Tests = $diagnosticsTests }
        @{ Name = "Redaction Metadata"; Tests = $redactionTests }
        @{ Name = "LowerCamelCase"; Tests = $camelCaseTests }
    )

    foreach ($group in $testGroups) {
        Write-Host ""
        Write-Host "Test Group: $($group.Name)" -ForegroundColor Yellow
        foreach ($test in $group.Tests) {
            $passed = & $test.Test $obj
            Assert-True $passed $test.Name
        }
    }

    # Secret tests need raw JSON
    Write-Host ""
    Write-Host "Test Group: Secret Redaction" -ForegroundColor Yellow
    foreach ($test in $secretTests) {
        $passed = & $test.Test $json
        Assert-True $passed $test.Name
    }

    # Show sample output
    Write-Host ""
    Write-Host "Sample JSON Output:" -ForegroundColor Cyan
    Write-Host $json

} else {
    Write-Host ""
    Write-Host "TDD Mode: Module not yet implemented" -ForegroundColor Yellow
    Write-Host "Expected module: $jsonModulePath" -ForegroundColor Yellow
    Write-Host ""

    $totalTests = $schemaTests.Count + $requestEffectiveTests.Count + $hostFingerprintTests.Count +
                  $resultsTests.Count + $outcomeReasonTests.Count + $summaryTests.Count +
                  $diagnosticsTests.Count + $redactionTests.Count + $secretTests.Count + $camelCaseTests.Count

    Write-Host "Tests defined: $totalTests" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Expected JSON Schema (v1.1):" -ForegroundColor Cyan

    $example = [ordered]@{
        schemaVersion = "1.1"
        schemaId = "richer-tunes.lidarr.e2e-run-manifest"
        timestamp = "2025-01-15T10:30:00.000Z"
        runId = "a1b2c3d4e5f6"
        runner = [ordered]@{
            name = "lidarr.plugin.common:e2e-runner.ps1"
            version = "dcaf488"
            args = @("-Gate", "bootstrap", "-Plugins", "Qobuzarr")
        }
        lidarr = [ordered]@{
            url = "http://[LOCALHOST]:8686"
            containerName = "lidarr-e2e-test"
            imageTag = "pr-plugins-3.1.1.4884"
            imageDigest = "sha256:abc123..."
            version = "2.9.6.4552"
            branch = "plugins"
        }
        request = [ordered]@{
            gate = "bootstrap"
            plugins = @("Qobuzarr", "Tidalarr")
        }
        effective = [ordered]@{
            gates = @("Schema", "Configure", "Search", "AlbumSearch")
            plugins = @("Qobuzarr", "Tidalarr")
        }
        redaction = [ordered]@{
            selfTestExecuted = $true
            selfTestPassed = $true
            patternsCount = 22
        }
        results = @(
            [ordered]@{
                gate = "Schema"
                plugin = "Qobuzarr"
                outcome = "success"
                outcomeReason = $null
                durationMs = 150
                errors = @()
                details = [ordered]@{
                    indexerFound = $true
                    downloadClientFound = $true
                }
            }
            [ordered]@{
                gate = "Search"
                plugin = "Qobuzarr"
                outcome = "skipped"
                outcomeReason = "Missing env vars: QOBUZARR_AUTH_TOKEN"
                durationMs = 5
                errors = @()
                details = [ordered]@{
                    credentialAllOf = @("authToken", "userId")
                }
            }
        )
        summary = [ordered]@{
            overallSuccess = $false
            totalGates = 2
            passed = 1
            failed = 0
            skipped = 1
            totalDurationMs = 155
        }
        diagnostics = [ordered]@{
            bundlePath = $null
            bundleCreated = $false
            redactionApplied = $true
            redactionSelfTestPassed = $true
        }
    }

    $example | ConvertTo-Json -Depth 8

    $script:testsFailed = $totalTests
}

# ============================================================================
# Summary
# ============================================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Passed: $script:testsPassed" -ForegroundColor Green
Write-Host "Failed: $script:testsFailed" -ForegroundColor $(if ($script:testsFailed -gt 0) { 'Red' } else { 'Green' })

if ($script:testsFailed -gt 0) { exit 1 }
exit 0
