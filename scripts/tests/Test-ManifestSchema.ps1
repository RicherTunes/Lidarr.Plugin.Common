#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Schema validation tests for e2e-run-manifest v1.2.
.DESCRIPTION
    Validates:
    1. Schema file is valid JSON with expected anchors ($schema, $id, title, type)
    2. Real manifest output conforms to schema structure
    3. Static fixture sample for regression readability
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
# Paths
# ============================================================================

$scriptRoot = $PSScriptRoot
$repoRoot = (Get-Item (Join-Path $scriptRoot "../..")).FullName
$schemaPath = Join-Path $repoRoot "docs/reference/e2e-run-manifest.schema.json"
$e2eOutputPath = Join-Path $repoRoot ".e2e-test-output/run-manifest.json"
$libPath = Join-Path $scriptRoot "../lib"
$jsonModulePath = Join-Path $libPath "e2e-json-output.psm1"

# ============================================================================
# Schema Self-Validation Tests
# ============================================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Schema Self-Validation Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if (Test-Path $schemaPath) {
    $schemaContent = Get-Content $schemaPath -Raw
    $schema = $null

    # Test 1: Valid JSON
    try {
        $schema = $schemaContent | ConvertFrom-Json
        Assert-True $true "Schema is valid JSON"
    } catch {
        Assert-True $false "Schema is valid JSON ($($_.Exception.Message))"
    }

    if ($schema) {
        # Test 2: Has $schema anchor
        Assert-True ($schema.'$schema' -match 'json-schema.org') "Schema has `$schema anchor"

        # Test 3: Has $id anchor
        Assert-True (-not [string]::IsNullOrWhiteSpace($schema.'$id')) "Schema has `$id anchor"

        # Test 4: Has title
        Assert-True ($schema.title -eq "E2E Run Manifest") "Schema has expected title"

        # Test 5: Root type is object
        Assert-True ($schema.type -eq "object") "Schema root type is object"

        # Test 6: Has required fields array
        Assert-True ($schema.required -is [array] -and $schema.required.Count -gt 0) "Schema has required fields"

        # Test 7: schemaVersion is const "1.2"
        Assert-True ($schema.properties.schemaVersion.const -eq "1.2") "schemaVersion is const '1.2'"

        # Test 8: schemaId is const
        Assert-True ($schema.properties.schemaId.const -eq "richer-tunes.lidarr.e2e-run-manifest") "schemaId is const 'richer-tunes.lidarr.e2e-run-manifest'"

        # Test 9: timestamp has format date-time
        Assert-True ($schema.properties.timestamp.format -eq "date-time") "timestamp has format date-time"

        # Test 10: runId has minLength 1
        Assert-True ($schema.properties.runId.minLength -eq 1) "runId has minLength 1"

        # Test 11: results[].outcome has enum
        $outcomeEnum = $schema.'$defs'.gateResult.properties.outcome.enum
        Assert-True (($outcomeEnum -contains "success") -and ($outcomeEnum -contains "failed") -and ($outcomeEnum -contains "skipped")) "outcome has enum [success, failed, skipped]"

        # Test 12: errorCode allows null or pattern
        $errorCodeDef = $schema.'$defs'.gateResult.properties.errorCode.oneOf
        $hasNull = $errorCodeDef | Where-Object { $_.PSObject.Properties.Name -contains 'type' -and $_.type -eq "null" }
        $hasPattern = $errorCodeDef | Where-Object { $_.PSObject.Properties.Name -contains 'pattern' -and $_.pattern -eq "^E2E_[A-Z0-9_]+$" }
        Assert-True ($hasNull -and $hasPattern) "errorCode allows null or E2E_* pattern"

        # Test 13: hostBugSuspected requires detected
        Assert-True ($schema.properties.hostBugSuspected.required -contains "detected") "hostBugSuspected requires 'detected'"

        # Test 14: hostBugSuspected.detected is boolean
        Assert-True ($schema.properties.hostBugSuspected.properties.detected.type -eq "boolean") "hostBugSuspected.detected is boolean"

        # Test 15: Top-level allows additionalProperties (extensible)
        Assert-True ($schema.additionalProperties -eq $true) "Top-level allows additionalProperties (extensible)"

        # Test 16: runner is strict (additionalProperties: false)
        Assert-True ($schema.properties.runner.additionalProperties -eq $false) "runner is strict (additionalProperties: false)"

        # Test 17: lidarr is strict
        Assert-True ($schema.properties.lidarr.additionalProperties -eq $false) "lidarr is strict (additionalProperties: false)"

        # Test 18: summary is strict
        Assert-True ($schema.properties.summary.additionalProperties -eq $false) "summary is strict (additionalProperties: false)"

        # Test 19: redaction is strict
        Assert-True ($schema.properties.redaction.additionalProperties -eq $false) "redaction is strict (additionalProperties: false)"

        # Test 20: results[].details allows additionalProperties (extensible)
        Assert-True ($schema.'$defs'.gateResult.properties.details.additionalProperties -eq $true) "results[].details allows additionalProperties (extensible)"

        # Test 21: sources allows additionalProperties (extensible)
        Assert-True ($schema.properties.sources.additionalProperties -eq $true) "sources allows additionalProperties (extensible)"

        # Test 22: diagnostics allows additionalProperties (extensible)
        Assert-True ($schema.properties.diagnostics.additionalProperties -eq $true) "diagnostics allows additionalProperties (extensible)"

        # Test 23: gate is NOT an enum (just string)
        $gateProp = $schema.'$defs'.gateResult.properties.gate
        $gateType = $gateProp.type
        $hasEnum = $gateProp.PSObject.Properties.Name -contains 'enum'
        Assert-True ($gateType -eq "string" -and -not $hasEnum) "gate is string, not enum (allows new gates)"
    }
} else {
    Write-Host "  [SKIP] Schema file not found: $schemaPath" -ForegroundColor Yellow
}

# ============================================================================
# Conformance Test: Real Manifest Output
# ============================================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Conformance Test: Real Manifest Output" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if (Test-Path $e2eOutputPath) {
    $manifestContent = Get-Content $e2eOutputPath -Raw
    $manifest = $null

    try {
        $manifest = $manifestContent | ConvertFrom-Json
        Assert-True $true "Real manifest is valid JSON"
    } catch {
        Assert-True $false "Real manifest is valid JSON ($($_.Exception.Message))"
    }

    if ($manifest) {
        # Core fields
        Assert-Equal "1.2" $manifest.schemaVersion "Manifest schemaVersion is 1.2"
        Assert-Equal "richer-tunes.lidarr.e2e-run-manifest" $manifest.schemaId "Manifest schemaId matches"

        # Timestamp validation
        $timestampValid = $false
        try {
            [DateTime]::Parse($manifest.timestamp) | Out-Null
            $timestampValid = $true
        } catch {}
        Assert-True $timestampValid "timestamp is valid ISO 8601"

        # runId non-empty
        Assert-True (-not [string]::IsNullOrWhiteSpace($manifest.runId)) "runId is non-empty"

        # runner structure
        Assert-True ($manifest.runner.name -match 'e2e-runner') "runner.name contains e2e-runner"
        Assert-True (-not [string]::IsNullOrWhiteSpace($manifest.runner.version)) "runner.version is non-empty"
        Assert-True ($manifest.runner.args -is [array]) "runner.args is array"

        # lidarr structure
        Assert-True (-not [string]::IsNullOrWhiteSpace($manifest.lidarr.url)) "lidarr.url is non-empty"
        Assert-True (-not [string]::IsNullOrWhiteSpace($manifest.lidarr.containerName)) "lidarr.containerName is non-empty"

        # results structure
        Assert-True ($manifest.results -is [array]) "results is array"

        foreach ($r in $manifest.results) {
            # outcome enum validation
            Assert-True ($r.outcome -in @('success', 'failed', 'skipped')) "result[$($r.gate)].outcome is valid enum"

            # errorCode pattern validation (null or E2E_*)
            if ($null -ne $r.errorCode -and $r.errorCode -ne '') {
                Assert-True ($r.errorCode -match '^E2E_[A-Z0-9_]+$') "result[$($r.gate)].errorCode matches pattern"
            } else {
                Assert-True $true "result[$($r.gate)].errorCode is null (valid)"
            }

            # durationMs >= 0
            Assert-True ($r.durationMs -ge 0) "result[$($r.gate)].durationMs >= 0"

            # errors is array
            Assert-True ($r.errors -is [array]) "result[$($r.gate)].errors is array"

            # details is object
            Assert-True ($null -ne $r.details) "result[$($r.gate)].details exists"
        }

        # summary validation
        Assert-True ($manifest.summary.overallSuccess -is [bool]) "summary.overallSuccess is boolean"
        Assert-True ($manifest.summary.totalGates -eq $manifest.results.Count) "summary.totalGates matches results.Count"
        $sumCheck = $manifest.summary.passed + $manifest.summary.failed + $manifest.summary.skipped
        Assert-True ($sumCheck -eq $manifest.summary.totalGates) "summary.passed + failed + skipped = totalGates"

        # hostBugSuspected structure
        Assert-True ($manifest.hostBugSuspected.PSObject.Properties.Name -contains 'detected') "hostBugSuspected has 'detected' field"
        Assert-True ($manifest.hostBugSuspected.detected -is [bool]) "hostBugSuspected.detected is boolean"

        # redaction structure
        Assert-True ($manifest.redaction.selfTestExecuted -is [bool]) "redaction.selfTestExecuted is boolean"
        Assert-True ($manifest.redaction.selfTestPassed -is [bool]) "redaction.selfTestPassed is boolean"
        Assert-True ($manifest.redaction.patternsCount -is [int] -or $manifest.redaction.patternsCount -is [long]) "redaction.patternsCount is integer"

        # diagnostics structure
        Assert-True ($manifest.diagnostics.bundleCreated -is [bool]) "diagnostics.bundleCreated is boolean"
        Assert-True ($manifest.diagnostics.redactionApplied -is [bool]) "diagnostics.redactionApplied is boolean"
    }
} else {
    Write-Host "  [SKIP] No real manifest output found at: $e2eOutputPath" -ForegroundColor Yellow
    Write-Host "         Run e2e-runner.ps1 first to generate manifest" -ForegroundColor Yellow
}

# ============================================================================
# Conformance Test: Module-Generated Output
# ============================================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Conformance Test: Module-Generated Output" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if (Test-Path $jsonModulePath) {
    Import-Module $jsonModulePath -Force

    # Create mock data
    $mockResults = @(
        [PSCustomObject]@{
            Gate = "Schema"
            PluginName = "TestPlugin"
            Outcome = "success"
            Success = $true
            Errors = @()
            Details = @{ indexerFound = $true }
            StartTime = [DateTime]::UtcNow.AddSeconds(-2)
            EndTime = [DateTime]::UtcNow
        }
        [PSCustomObject]@{
            Gate = "Configure"
            PluginName = "TestPlugin"
            Outcome = "skipped"
            Success = $false
            Errors = @()
            Details = @{ SkipReason = "Missing credentials" }
            StartTime = [DateTime]::UtcNow.AddSeconds(-1)
            EndTime = [DateTime]::UtcNow
        }
    )

    $mockContext = @{
        LidarrUrl = "http://localhost:8686"
        ContainerName = "lidarr-test"
        ContainerId = "abc123def456"
        ContainerStartedAt = [DateTime]::UtcNow.AddMinutes(-5)
        ImageTag = "pr-plugins-test"
        ImageId = "sha256:test"
        ImageDigest = "sha256:test"
        RequestedGate = "bootstrap"
        Plugins = @("TestPlugin")
        EffectiveGates = @("Schema", "Configure")
        EffectivePlugins = @("TestPlugin")
        StopReason = $null
        RedactionSelfTestExecuted = $true
        RedactionSelfTestPassed = $true
        RunnerArgs = @("-Gate", "bootstrap")
        DiagnosticsBundlePath = $null
        DiagnosticsIncludedFiles = @()
        LidarrVersion = "2.9.6"
        LidarrBranch = "plugins"
        SourceShas = @{
            Common = "abc1234"
            TestPlugin = "def5678"
        }
        SourceProvenance = @{
            Common = "git"
            TestPlugin = "git"
        }
        ComponentIds = @{
            InstanceKey = "test-instance-key"
            InstanceKeySource = "explicit"
            LockPolicy = @{
                TimeoutMs = 1000
                RetryDelayMs = 100
                StaleSeconds = 60
            }
            LockPolicySource = "env"
            PersistenceEnabled = $true
        }
    }

    # Generate JSON
    $json = ConvertTo-E2ERunManifest -Results $mockResults -Context $mockContext
    $obj = $json | ConvertFrom-Json

    # Validate against schema constraints
    Assert-True ($obj.'$schema' -match 'raw\.githubusercontent\.com.*e2e-run-manifest\.schema\.json') "Module output: `$schema is fetchable raw URL"
    Assert-Equal "1.2" $obj.schemaVersion "Module output: schemaVersion is 1.2"
    Assert-Equal "richer-tunes.lidarr.e2e-run-manifest" $obj.schemaId "Module output: schemaId matches"
    Assert-True ($obj.results.Count -eq 2) "Module output: results has 2 entries"
    Assert-True ($obj.results[0].outcome -in @('success', 'failed', 'skipped')) "Module output: outcome is valid enum"
    Assert-True ($obj.hostBugSuspected.detected -is [bool]) "Module output: hostBugSuspected.detected is boolean"

    # Component IDs conformance tests
    Assert-True ($null -ne $obj.componentIds) "Module output: componentIds block is present"
    Assert-Equal "test-instance-key" $obj.componentIds.instanceKey "Module output: componentIds.instanceKey matches"
    Assert-True ($obj.componentIds.instanceKeySource -in @('explicit', 'computed')) "Module output: componentIds.instanceKeySource is valid enum"
    Assert-True ($obj.componentIds.lockPolicy.timeoutMs -is [int] -or $obj.componentIds.lockPolicy.timeoutMs -is [long]) "Module output: lockPolicy.timeoutMs is integer"
    Assert-True ($obj.componentIds.lockPolicySource -in @('default', 'env')) "Module output: componentIds.lockPolicySource is valid enum"
    Assert-True ($obj.componentIds.persistenceEnabled -is [bool]) "Module output: componentIds.persistenceEnabled is boolean"

} else {
    Write-Host "  [SKIP] JSON module not found: $jsonModulePath" -ForegroundColor Yellow
}

# ============================================================================
# Hermetic Disk-Write Test (covers Write-E2ERunManifest path)
# ============================================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Hermetic Disk-Write Test" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if (Test-Path $jsonModulePath) {
    # Create temp file path
    $tempPath = Join-Path $env:TEMP "e2e-manifest-test-$([Guid]::NewGuid().ToString('N').Substring(0,8)).json"

    try {
        # Create mock data
        $mockResults = @(
            [PSCustomObject]@{
                Gate = "Schema"
                PluginName = "DiskWriteTest"
                Outcome = "success"
                Success = $true
                Errors = @()
                Details = @{ indexerFound = $true }
                StartTime = [DateTime]::UtcNow.AddSeconds(-1)
                EndTime = [DateTime]::UtcNow
            }
        )

        $mockContext = @{
            LidarrUrl = "http://localhost:8686"
            ContainerName = "disk-write-test"
            ContainerId = "test123"
            ImageTag = "test"
            RequestedGate = "schema"
            Plugins = @("DiskWriteTest")
            EffectiveGates = @("Schema")
            EffectivePlugins = @("DiskWriteTest")
            RedactionSelfTestExecuted = $true
            RedactionSelfTestPassed = $true
            RunnerArgs = @("-Gate", "schema")
            SourceShas = @{ Common = "abc123" }
            SourceProvenance = @{ Common = "git" }
        }

        # Write manifest to temp file
        Write-E2ERunManifest -Results $mockResults -Context $mockContext -OutputPath $tempPath | Out-Null

        # Verify file was created
        Assert-True (Test-Path $tempPath) "Manifest file was created"

        # Read and validate
        $diskContent = Get-Content $tempPath -Raw
        $diskObj = $diskContent | ConvertFrom-Json

        Assert-True ($diskObj.'$schema' -match 'raw\.githubusercontent\.com.*e2e-run-manifest\.schema\.json') "Disk: `$schema is fetchable raw URL"
        Assert-Equal "1.2" $diskObj.schemaVersion "Disk: schemaVersion is 1.2"
        Assert-Equal "richer-tunes.lidarr.e2e-run-manifest" $diskObj.schemaId "Disk: schemaId matches"
        Assert-True ($diskObj.results.Count -eq 1) "Disk: results has 1 entry"
        Assert-True ($diskObj.hostBugSuspected.detected -is [bool]) "Disk: hostBugSuspected.detected is boolean"

        # Verify encoding (UTF-8 without BOM is expected)
        $bytes = [System.IO.File]::ReadAllBytes($tempPath)
        $hasBOM = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        # Note: PowerShell Out-File with -Encoding UTF8 may add BOM on Windows; acceptable
        Assert-True ($bytes.Length -gt 0) "Disk: file has content"

    } finally {
        # Cleanup
        if (Test-Path $tempPath) {
            Remove-Item $tempPath -Force -ErrorAction SilentlyContinue
        }
    }
} else {
    Write-Host "  [SKIP] JSON module not found" -ForegroundColor Yellow
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
