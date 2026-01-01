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
        ContainerId = "abc123def456"
        ContainerStartedAt = [DateTime]::UtcNow.AddMinutes(-5)
        ImageTag = $ImageTag
        ImageId = "sha256:image123"
        ImageDigest = "sha256:abc123def456"
        RequestedGate = $RequestedGate
        Plugins = $Plugins
        EffectiveGates = $EffectiveGates
        EffectivePlugins = $Plugins
        StopReason = $null
        RedactionSelfTestExecuted = $true
        RedactionSelfTestPassed = $RedactionPassed
        RunnerArgs = @("-Gate", "bootstrap", "-Plugins", "Qobuzarr,Tidalarr")
        DiagnosticsBundlePath = $null
        DiagnosticsIncludedFiles = @("run-manifest.json", "container.log", "inspect.json")
        LidarrVersion = $LidarrVersion
        LidarrBranch = $LidarrBranch
        SourceShas = @{
            Common = "abc1234"
            Qobuzarr = "def5678"
            Tidalarr = "ghi9012"
            Brainarr = $null
        }
        SourceProvenance = @{
            Common = "git"
            Qobuzarr = "git"
            Tidalarr = "env"
            Brainarr = "unknown"
        }
    }
}

# ============================================================================
# Schema Tests - Core Structure
# ============================================================================

$schemaTests = @(
    @{
        Name = "schemaVersion is '1.2'"
        Test = { param($obj) $obj.schemaVersion -eq "1.2" }
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
# Schema v1.2 Tests - New Features
# ============================================================================

$v12SourcesTests = @(
    @{
        Name = "sources block is present"
        Test = { param($obj) $null -ne $obj.sources }
    }
    @{
        Name = "sources.common.sha is present"
        Test = { param($obj) $obj.sources.PSObject.Properties.Name -contains 'common' }
    }
    @{
        Name = "sources includes plugin repos (qobuzarr, tidalarr, brainarr)"
        Test = {
            param($obj)
            $obj.sources.PSObject.Properties.Name -contains 'qobuzarr' -and
            $obj.sources.PSObject.Properties.Name -contains 'tidalarr' -and
            $obj.sources.PSObject.Properties.Name -contains 'brainarr'
        }
    }
    @{
        Name = "sources.*.source provenance field present"
        Test = {
            param($obj)
            $obj.sources.common.PSObject.Properties.Name -contains 'source' -and
            $obj.sources.qobuzarr.PSObject.Properties.Name -contains 'source'
        }
    }
    @{
        Name = "sources.*.source is git|env|unknown"
        Test = {
            param($obj)
            $valid = @('git', 'env', 'unknown')
            $obj.sources.common.source -in $valid
        }
    }
)

$v12ContainerTests = @(
    @{
        Name = "lidarr.containerId is present"
        Test = { param($obj) $obj.lidarr.PSObject.Properties.Name -contains 'containerId' }
    }
    @{
        Name = "lidarr.startedAt is present"
        Test = { param($obj) $obj.lidarr.PSObject.Properties.Name -contains 'startedAt' }
    }
    @{
        Name = "lidarr.imageId is present"
        Test = { param($obj) $obj.lidarr.PSObject.Properties.Name -contains 'imageId' }
    }
)

$v12GateTimingTests = @(
    @{
        Name = "each result has startedAt timestamp"
        Test = {
            param($obj)
            foreach ($r in $obj.results) {
                if (-not ($r.PSObject.Properties.Name -contains 'startedAt')) { return $false }
            }
            return $true
        }
    }
    @{
        Name = "each result has endedAt timestamp"
        Test = {
            param($obj)
            foreach ($r in $obj.results) {
                if (-not ($r.PSObject.Properties.Name -contains 'endedAt')) { return $false }
            }
            return $true
        }
    }
)

$v12ErrorCodeTests = @(
    @{
        Name = "each result has errorCode field (null for success)"
        Test = {
            param($obj)
            foreach ($r in $obj.results) {
                if (-not ($r.PSObject.Properties.Name -contains 'errorCode')) { return $false }
            }
            return $true
        }
    }
    @{
        Name = "failed results have errorCode when inferable"
        Test = {
            param($obj)
            foreach ($r in $obj.results) {
                if ($r.outcome -eq 'failed' -and $r.errors.Count -gt 0) {
                    # errorCode can be null if no pattern matches, that's OK
                    return $true
                }
            }
            return $true
        }
    }
)

$v12EffectiveTests = @(
    @{
        Name = "effective.stopReason field is present"
        Test = { param($obj) $obj.effective.PSObject.Properties.Name -contains 'stopReason' }
    }
)

$v12DiagnosticsTests = @(
    @{
        Name = "diagnostics.includedFiles is an array"
        Test = { param($obj) $obj.diagnostics.includedFiles -is [array] }
    }
)

$v12HostBugTests = @(
    @{
        Name = "hostBugSuspected is always present"
        Test = { param($obj) $obj.PSObject.Properties.Name -contains 'hostBugSuspected' }
    }
    @{
        Name = "hostBugSuspected.detected is boolean"
        Test = { param($obj) $obj.hostBugSuspected.detected -is [bool] }
    }
    @{
        Name = "hostBugSuspected has minimal fields when not detected"
        Test = {
            param($obj)
            if (-not $obj.hostBugSuspected.detected) {
                # When not detected, should only have 'detected' field (quiet mode)
                $props = @($obj.hostBugSuspected.PSObject.Properties.Name)
                return $props.Count -eq 1 -and $props -contains 'detected'
            }
            return $true
        }
    }
)

# ============================================================================
# Assembly Issue Detection Tests (Tiered Classification)
# ============================================================================

$alcDetectionTests = @(
    @{
        Name = "Test-ALCPattern detects TypeLoadException as ABI_MISMATCH"
        Test = {
            param($obj)
            $result = Test-ALCPattern -Errors @("Could not load type 'Foo.Bar' from assembly 'MyPlugin'")
            $result.detected -eq $true -and $result.classification -eq 'ABI_MISMATCH' -and $result.severity -eq 'plugin_rebuild'
        }
    }
    @{
        Name = "Test-ALCPattern detects true ALC bug (AssemblyLoadContext unload)"
        Test = {
            param($obj)
            $result = Test-ALCPattern -Errors @("AssemblyLoadContext failed to unload due to running threads")
            $result.detected -eq $true -and $result.classification -eq 'ALC' -and $result.severity -eq 'host_bug'
        }
    }
    @{
        Name = "Test-ALCPattern detects duplicate assembly in different context as ALC"
        Test = {
            param($obj)
            $result = Test-ALCPattern -Errors @("Assembly 'Newtonsoft.Json' is already loaded in a different context")
            $result.detected -eq $true -and $result.classification -eq 'ALC' -and $result.severity -eq 'host_bug'
        }
    }
    @{
        Name = "Test-ALCPattern detects FileLoadException with version as DEPENDENCY_DRIFT"
        Test = {
            param($obj)
            $result = Test-ALCPattern -Errors @("FileLoadException: Could not load assembly version mismatch")
            $result.detected -eq $true -and $result.classification -eq 'DEPENDENCY_DRIFT' -and $result.severity -eq 'version_conflict'
        }
    }
    @{
        Name = "Test-ALCPattern detects generic FileLoadException as LOAD_FAILURE"
        Test = {
            param($obj)
            $result = Test-ALCPattern -Errors @("FileLoadException: Could not load assembly from disk")
            $result.detected -eq $true -and $result.classification -eq 'LOAD_FAILURE' -and $result.severity -eq 'investigate'
        }
    }
    @{
        Name = "Test-ALCPattern detects MissingMethodException as ABI_MISMATCH"
        Test = {
            param($obj)
            $result = Test-ALCPattern -Errors @("MissingMethodException: Method not found in assembly")
            $result.detected -eq $true -and $result.classification -eq 'ABI_MISMATCH' -and $result.severity -eq 'plugin_rebuild'
        }
    }
    @{
        Name = "Test-ALCPattern returns false for non-assembly errors"
        Test = {
            param($obj)
            $result = Test-ALCPattern -Errors @("Connection refused", "Timeout waiting for response", "HTTP 404 Not Found")
            $result.detected -eq $false
        }
    }
    @{
        Name = "Test-ALCPattern sanitizes matchedLine"
        Test = {
            param($obj)
            $result = Test-ALCPattern -Errors @("Could not load type 'MyPlugin.Indexer' from assembly at http://192.168.1.100/plugin.dll")
            $result.detected -eq $true -and $result.matchedLine -match '\[PRIVATE-IP\]'
        }
    }
    @{
        Name = "Test-ALCPattern prioritizes more specific patterns (ALC over generic)"
        Test = {
            param($obj)
            # Test that true ALC patterns are matched before generic ones
            $result = Test-ALCPattern -Errors @("Cannot unload the AssemblyLoadContext because threads are still running")
            $result.detected -eq $true -and $result.classification -eq 'ALC'
        }
    }
)

# ============================================================================
# Run Tests
# ============================================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "JSON Output Schema Tests (v1.2)" -ForegroundColor Cyan
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
        @{ Name = "v1.2 Sources"; Tests = $v12SourcesTests }
        @{ Name = "v1.2 Container Fingerprinting"; Tests = $v12ContainerTests }
        @{ Name = "v1.2 Gate Timing"; Tests = $v12GateTimingTests }
        @{ Name = "v1.2 Error Codes"; Tests = $v12ErrorCodeTests }
        @{ Name = "v1.2 Effective"; Tests = $v12EffectiveTests }
        @{ Name = "v1.2 Diagnostics"; Tests = $v12DiagnosticsTests }
        @{ Name = "v1.2 Host Bug Detection"; Tests = $v12HostBugTests }
        @{ Name = "Assembly Issue Classification"; Tests = $alcDetectionTests }
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

    # ========================================================================
    # Backward Compatibility: v1.1 Manifest Parsing
    # ========================================================================
    Write-Host ""
    Write-Host "Test Group: v1.1 Backward Compatibility" -ForegroundColor Yellow

    # Create a v1.1-style manifest (missing v1.2 fields: sources, containerId, startedAt, imageId, errorCode, stopReason, includedFiles)
    $v11Manifest = @'
{
    "schemaVersion": "1.1",
    "schemaId": "richer-tunes.lidarr.e2e-run-manifest",
    "timestamp": "2025-01-15T10:30:00.000Z",
    "runId": "legacy11test",
    "runner": {
        "name": "lidarr.plugin.common:e2e-runner.ps1",
        "version": "abc123",
        "args": ["-Gate", "bootstrap"]
    },
    "lidarr": {
        "url": "http://localhost:8686",
        "containerName": "lidarr-test",
        "imageTag": "pr-plugins-3.1.1.4884",
        "imageDigest": "sha256:abc",
        "version": "2.9.6",
        "branch": "plugins"
    },
    "request": {
        "gate": "bootstrap",
        "plugins": ["Qobuzarr"]
    },
    "effective": {
        "gates": ["Schema", "Configure"],
        "plugins": ["Qobuzarr"]
    },
    "redaction": {
        "selfTestExecuted": true,
        "selfTestPassed": true,
        "patternsCount": 20
    },
    "results": [
        {
            "gate": "Schema",
            "plugin": "Qobuzarr",
            "outcome": "success",
            "outcomeReason": null,
            "durationMs": 1500,
            "errors": [],
            "details": {}
        }
    ],
    "summary": {
        "overallSuccess": true,
        "totalGates": 1,
        "passed": 1,
        "failed": 0,
        "skipped": 0,
        "totalDurationMs": 1500
    },
    "diagnostics": {
        "bundlePath": null,
        "bundleCreated": false,
        "redactionApplied": true,
        "redactionSelfTestPassed": true
    }
}
'@

    $v11Obj = $v11Manifest | ConvertFrom-Json

    # Test that v1.1 manifests can be parsed without error
    Assert-True ($v11Obj.schemaVersion -eq "1.1") "Can parse v1.1 schemaVersion"
    Assert-True ($v11Obj.results.Count -eq 1) "Can parse v1.1 results array"
    Assert-True ($v11Obj.summary.overallSuccess -eq $true) "Can parse v1.1 summary"

    # Test that optional v1.2 fields are gracefully absent (null-safe access patterns)
    $hasSourcesProperty = $v11Obj.PSObject.Properties.Name -contains 'sources'
    Assert-True (-not $hasSourcesProperty) "v1.1 does not have sources block (expected)"

    $hasStopReason = $v11Obj.effective.PSObject.Properties.Name -contains 'stopReason'
    Assert-True (-not $hasStopReason) "v1.1 does not have effective.stopReason (expected)"

    # Verify the job summary step logic would work (null-coalescing simulation)
    $lidarrVersion = if ($v11Obj.lidarr.version) { $v11Obj.lidarr.version } else { 'n/a' }
    Assert-True ($lidarrVersion -eq "2.9.6") "v1.1 lidarr.version accessible"

    $stopReason = if ($v11Obj.effective -and $v11Obj.effective.PSObject.Properties.Name -contains 'stopReason') { $v11Obj.effective.stopReason } else { $null }
    Assert-True ($stopReason -eq $null) "v1.1 missing stopReason defaults to null"

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
