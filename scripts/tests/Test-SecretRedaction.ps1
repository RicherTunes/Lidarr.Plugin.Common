#!/usr/bin/env pwsh
# Test that secrets are properly redacted in JSON output

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot ".." "lib" "e2e-json-output.psm1") -Force
Import-Module (Join-Path $PSScriptRoot ".." "lib" "e2e-diagnostics.psm1") -Force

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Secret Redaction Audit" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Create test with various secret patterns
$testResults = @(
    [PSCustomObject]@{
        Gate = 'Search'
        PluginName = 'Qobuzarr'
        Outcome = 'skipped'
        Success = $false
        Errors = @()
        Details = @{
            SkipReason = 'Missing env vars: authToken'
            CredentialAllOf = @('authToken', 'userId', 'apiKey')
            # These should NOT appear in output (they get converted to lowerCamelCase)
            authToken = 'secret123abc'
            password = 'mypassword'
            apiKey = 'abcd1234567890abcd1234567890ab'
        }
        StartTime = [DateTime]::UtcNow.AddSeconds(-1)
        EndTime = [DateTime]::UtcNow
    }
)

$testContext = @{
    LidarrUrl = 'http://192.168.1.100:8686'  # Should be redacted
    ContainerName = 'test'
    ImageTag = 'test'
    ImageDigest = 'sha256:abc'
    RequestedGate = 'search'
    Plugins = @('Qobuzarr')
    EffectiveGates = @('Search')
    EffectivePlugins = @('Qobuzarr')
    RedactionSelfTestExecuted = $true
    RedactionSelfTestPassed = $true
    RunnerArgs = @('-ApiKey', 'abcd1234567890abcd1234567890ab', '-Gate', 'search')
    DiagnosticsBundlePath = $null
    LidarrVersion = '2.9.6'
    LidarrBranch = 'plugins'
}

$json = ConvertTo-E2ERunManifest -Results $testResults -Context $testContext
$obj = $json | ConvertFrom-Json

$passed = 0
$failed = 0

function Test-Assertion {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "  [PASS] $Message" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        $script:failed++
    }
}

Write-Host ""
Write-Host "Test Group: Secret Value Leakage" -ForegroundColor Yellow

Test-Assertion ($json -notmatch 'secret123abc') "authToken value not leaked"
Test-Assertion ($json -notmatch 'mypassword') "password value not leaked"
Test-Assertion ($json -notmatch 'abcd1234567890abcd1234567890ab') "API key not leaked in raw form"
Test-Assertion ($json -notmatch '192\.168\.1\.100') "Private IP not leaked"

Write-Host ""
Write-Host "Test Group: Redaction Applied" -ForegroundColor Yellow

Test-Assertion ($obj.lidarr.url -match '\[PRIVATE-IP\]') "lidarr.url has private IP redacted"
Test-Assertion (($obj.runner.args -join ' ') -match '\[REDACTED\]') "runner.args has API key redacted"
Test-Assertion ($obj.results[0].outcomeReason -ne $null) "outcomeReason is present"
Test-Assertion ($obj.results[0].outcomeReason -notmatch '=[a-zA-Z0-9]{8,}') "outcomeReason has no secret values"

Write-Host ""
Write-Host "Test Group: Credential Field Names Only" -ForegroundColor Yellow

$creds = $obj.results[0].details.credentialAllOf
Test-Assertion ($creds -contains 'authToken') "credentialAllOf contains field name 'authToken'"
Test-Assertion ($creds -contains 'userId') "credentialAllOf contains field name 'userId'"
Test-Assertion ($creds -notcontains 'secret123abc') "credentialAllOf does not contain secret values"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Audit Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'Green' })

if ($failed -gt 0) { exit 1 }
exit 0
