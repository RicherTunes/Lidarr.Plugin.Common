#Requires -Version 7.0
<#
.SYNOPSIS
    Tests for E2E error code classification and structured emission.
.DESCRIPTION
    Validates:
    - Explicit E2E error code extraction from metadata
    - Pattern-based error code inference (fallback)
    - Source tracking (explicit vs inferred)
    - New error codes (E2E_RATE_LIMITED, E2E_PROVIDER_UNAVAILABLE, etc.)
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Import test utilities
$testUtilsPath = Join-Path $PSScriptRoot "test-utils.psm1"
if (Test-Path $testUtilsPath) {
    Import-Module $testUtilsPath -Force
}

# Import the error classifier module
$classifierPath = Join-Path (Split-Path $PSScriptRoot -Parent) "lib/e2e-error-classifier.psm1"
Import-Module $classifierPath -Force

$script:TestResults = @{
    Passed = 0
    Failed = 0
    Errors = @()
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) {
        throw "Assertion failed: $Message`nExpected: $Expected`nActual: $Actual"
    }
}

function Assert-True {
    param($Condition, [string]$Message)
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Assert-Null {
    param($Value, [string]$Message)
    if ($null -ne $Value) {
        throw "Assertion failed: $Message`nExpected null but got: $Value"
    }
}

function Run-Test {
    param([string]$Name, [scriptblock]$Test)
    try {
        & $Test
        $script:TestResults.Passed++
        Write-Host "  [PASS] $Name" -ForegroundColor Green
    }
    catch {
        $script:TestResults.Failed++
        $script:TestResults.Errors += "$Name : $_"
        Write-Host "  [FAIL] $Name : $_" -ForegroundColor Red
    }
}

Write-Host "`n=== E2E Error Code Tests ===" -ForegroundColor Cyan

# ============================================================================
# Get-E2EErrorCodeMetadataKey Tests
# ============================================================================
Write-Host "`n--- Metadata Key Tests ---" -ForegroundColor Yellow

Run-Test "Get-E2EErrorCodeMetadataKey returns expected key" {
    $key = Get-E2EErrorCodeMetadataKey
    Assert-Equal "e2eErrorCode" $key "Metadata key should be 'e2eErrorCode'"
}

# ============================================================================
# Get-ExplicitE2EErrorCode Tests
# ============================================================================
Write-Host "`n--- Explicit Error Code Extraction Tests ---" -ForegroundColor Yellow

Run-Test "Get-ExplicitE2EErrorCode returns null for null metadata" {
    $result = Get-ExplicitE2EErrorCode -Metadata $null
    Assert-Null $result "Should return null for null metadata"
}

Run-Test "Get-ExplicitE2EErrorCode extracts e2eErrorCode from hashtable" {
    $metadata = @{ e2eErrorCode = "E2E_AUTH_MISSING" }
    $result = Get-ExplicitE2EErrorCode -Metadata $metadata
    Assert-Equal "E2E_AUTH_MISSING" $result "Should extract e2eErrorCode"
}

Run-Test "Get-ExplicitE2EErrorCode extracts ErrorCode from hashtable (legacy)" {
    $metadata = @{ ErrorCode = "E2E_CONFIG_INVALID" }
    $result = Get-ExplicitE2EErrorCode -Metadata $metadata
    Assert-Equal "E2E_CONFIG_INVALID" $result "Should extract ErrorCode"
}

Run-Test "Get-ExplicitE2EErrorCode prefers e2eErrorCode over ErrorCode" {
    $metadata = @{
        e2eErrorCode = "E2E_AUTH_MISSING"
        ErrorCode = "E2E_CONFIG_INVALID"
    }
    $result = Get-ExplicitE2EErrorCode -Metadata $metadata
    Assert-Equal "E2E_AUTH_MISSING" $result "Should prefer e2eErrorCode"
}

Run-Test "Get-ExplicitE2EErrorCode extracts from PSObject" {
    $metadata = [PSCustomObject]@{ e2eErrorCode = "E2E_RATE_LIMITED" }
    $result = Get-ExplicitE2EErrorCode -Metadata $metadata
    Assert-Equal "E2E_RATE_LIMITED" $result "Should extract from PSObject"
}

Run-Test "Get-ExplicitE2EErrorCode returns null for empty hashtable" {
    $result = Get-ExplicitE2EErrorCode -Metadata @{}
    Assert-Null $result "Should return null for empty hashtable"
}

# ============================================================================
# Get-E2EErrorClassification with Metadata Tests
# ============================================================================
Write-Host "`n--- Classification with Metadata Tests ---" -ForegroundColor Yellow

Run-Test "Classification uses explicit code when present" {
    $metadata = @{ e2eErrorCode = "E2E_PROVIDER_UNAVAILABLE" }
    $result = Get-E2EErrorClassification -Messages @() -Metadata $metadata
    Assert-Equal "E2E_PROVIDER_UNAVAILABLE" $result.errorCode "Should use explicit code"
    Assert-Equal "explicit" $result.source "Source should be 'explicit'"
}

Run-Test "Classification falls back to inference when no metadata" {
    $result = Get-E2EErrorClassification -Messages @("unauthorized access") -Metadata $null
    Assert-Equal "E2E_AUTH_MISSING" $result.errorCode "Should infer E2E_AUTH_MISSING"
    Assert-Equal "inferred" $result.source "Source should be 'inferred'"
}

Run-Test "Classification falls back to inference when metadata has no code" {
    $metadata = @{ someOtherField = "value" }
    $result = Get-E2EErrorClassification -Messages @("timeout occurred") -Metadata $metadata
    Assert-Equal "E2E_API_TIMEOUT" $result.errorCode "Should infer E2E_API_TIMEOUT"
    Assert-Equal "inferred" $result.source "Source should be 'inferred'"
}

Run-Test "Explicit E2E_AUTH_MISSING sets isCredentialPrereq" {
    $metadata = @{ e2eErrorCode = "E2E_AUTH_MISSING" }
    $result = Get-E2EErrorClassification -Messages @() -Metadata $metadata
    Assert-True $result.isCredentialPrereq "E2E_AUTH_MISSING should set isCredentialPrereq"
}

Run-Test "Explicit non-auth code does not set isCredentialPrereq" {
    $metadata = @{ e2eErrorCode = "E2E_RATE_LIMITED" }
    $result = Get-E2EErrorClassification -Messages @() -Metadata $metadata
    Assert-True (-not $result.isCredentialPrereq) "E2E_RATE_LIMITED should not set isCredentialPrereq"
}

# ============================================================================
# New Error Code Pattern Tests
# ============================================================================
Write-Host "`n--- New Error Code Pattern Tests ---" -ForegroundColor Yellow

Run-Test "E2E_RATE_LIMITED inferred from 'rate limit'" {
    $result = Get-E2EErrorClassification -Messages @("rate limit exceeded")
    Assert-Equal "E2E_RATE_LIMITED" $result.errorCode
}

Run-Test "E2E_RATE_LIMITED inferred from 429 status" {
    $result = Get-E2EErrorClassification -Messages @("received 429 too many requests")
    Assert-Equal "E2E_RATE_LIMITED" $result.errorCode
}

Run-Test "E2E_RATE_LIMITED inferred from quota exceeded" {
    $result = Get-E2EErrorClassification -Messages @("quota exceeded for this month")
    Assert-Equal "E2E_RATE_LIMITED" $result.errorCode
}

Run-Test "E2E_PROVIDER_UNAVAILABLE inferred from service unavailable" {
    $result = Get-E2EErrorClassification -Messages @("service unavailable, try again later")
    Assert-Equal "E2E_PROVIDER_UNAVAILABLE" $result.errorCode
}

Run-Test "E2E_PROVIDER_UNAVAILABLE inferred from 503 status" {
    $result = Get-E2EErrorClassification -Messages @("HTTP 503 from upstream")
    Assert-Equal "E2E_PROVIDER_UNAVAILABLE" $result.errorCode
}

Run-Test "E2E_CANCELLED inferred from cancelled" {
    $result = Get-E2EErrorClassification -Messages @("operation was cancelled by user")
    Assert-Equal "E2E_CANCELLED" $result.errorCode
}

Run-Test "E2E_CANCELLED inferred from canceled (US spelling)" {
    $result = Get-E2EErrorClassification -Messages @("request was canceled")
    Assert-Equal "E2E_CANCELLED" $result.errorCode
}

Run-Test "E2E_COMPONENT_AMBIGUOUS inferred from ambiguous" {
    $result = Get-E2EErrorClassification -Messages @("ambiguous component selection")
    Assert-Equal "E2E_COMPONENT_AMBIGUOUS" $result.errorCode
}

Run-Test "E2E_LOAD_FAILURE inferred from assembly load" {
    $result = Get-E2EErrorClassification -Messages @("assembly load failed for Plugin.dll")
    Assert-Equal "E2E_LOAD_FAILURE" $result.errorCode
}

Run-Test "E2E_LOAD_FAILURE inferred from type load" {
    $result = Get-E2EErrorClassification -Messages @("type load exception in TidalIndexer")
    Assert-Equal "E2E_LOAD_FAILURE" $result.errorCode
}

# ============================================================================
# Existing Pattern Tests (Regression)
# ============================================================================
Write-Host "`n--- Existing Pattern Regression Tests ---" -ForegroundColor Yellow

Run-Test "E2E_AUTH_MISSING still inferred from unauthorized" {
    $result = Get-E2EErrorClassification -Messages @("unauthorized access to resource")
    Assert-Equal "E2E_AUTH_MISSING" $result.errorCode
    Assert-True $result.isCredentialPrereq
}

Run-Test "E2E_API_TIMEOUT still inferred from timeout" {
    $result = Get-E2EErrorClassification -Messages @("request timeout after 30s")
    Assert-Equal "E2E_API_TIMEOUT" $result.errorCode
}

Run-Test "E2E_CONFIG_INVALID still inferred from config error" {
    $result = Get-E2EErrorClassification -Messages @("configuration validation failed")
    Assert-Equal "E2E_CONFIG_INVALID" $result.errorCode
}

Run-Test "E2E_DOCKER_UNAVAILABLE still inferred from docker" {
    $result = Get-E2EErrorClassification -Messages @("docker daemon not running")
    Assert-Equal "E2E_DOCKER_UNAVAILABLE" $result.errorCode
}

Run-Test "Empty messages returns null errorCode" {
    $result = Get-E2EErrorClassification -Messages @()
    Assert-Null $result.errorCode "Empty messages should return null errorCode"
    Assert-Null $result.source "Empty messages should return null source"
}

Run-Test "No pattern match returns null with null source" {
    $result = Get-E2EErrorClassification -Messages @("some random error without patterns")
    Assert-Null $result.errorCode "Unmatched error should return null errorCode"
    Assert-Null $result.source "Unmatched error should return null source"
}

# ============================================================================
# Results Summary
# ============================================================================
Write-Host "`n=== Test Results ===" -ForegroundColor Cyan
Write-Host "Passed: $($script:TestResults.Passed)" -ForegroundColor Green
Write-Host "Failed: $($script:TestResults.Failed)" -ForegroundColor $(if ($script:TestResults.Failed -gt 0) { 'Red' } else { 'Green' })

if ($script:TestResults.Errors.Count -gt 0) {
    Write-Host "`nErrors:" -ForegroundColor Red
    foreach ($err in $script:TestResults.Errors) {
        Write-Host "  - $err" -ForegroundColor Red
    }
}

# Exit with appropriate code
if ($script:TestResults.Failed -gt 0) {
    exit 1
}
exit 0
