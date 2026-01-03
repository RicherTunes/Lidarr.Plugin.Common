#Requires -Version 7.0
<#
.SYNOPSIS
    Tests AppleMusicarr schema field name consistency.
.DESCRIPTION
    Validates that the E2E runner's expected AppleMusicarr field names match:
    1. The golden fixture (source of truth)
    2. The E2E runner's $script:PluginConfig settings

    This test catches regressions when:
    - AppleMusicPluginSettings.cs field names change
    - E2E runner config drifts from actual schema
    - Sensitive field classification is incorrect
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

function Assert-Contains {
    param([array]$Collection, $Item, [string]$Message)
    if ($Item -notin $Collection) {
        throw "Assertion failed: $Message`nCollection does not contain: $Item`nCollection: $($Collection -join ', ')"
    }
}

function Assert-NotContains {
    param([array]$Collection, $Item, [string]$Message)
    if ($Item -in $Collection) {
        throw "Assertion failed: $Message`nCollection should not contain: $Item`nCollection: $($Collection -join ', ')"
    }
}

function Assert-ArrayEqual {
    param([array]$Expected, [array]$Actual, [string]$Message)
    $expectedSorted = @($Expected | Sort-Object)
    $actualSorted = @($Actual | Sort-Object)

    if ($expectedSorted.Count -ne $actualSorted.Count) {
        throw "Assertion failed: $Message`nExpected count: $($expectedSorted.Count)`nActual count: $($actualSorted.Count)`nExpected: $($expectedSorted -join ', ')`nActual: $($actualSorted -join ', ')"
    }

    for ($i = 0; $i -lt $expectedSorted.Count; $i++) {
        if ($expectedSorted[$i] -ne $actualSorted[$i]) {
            throw "Assertion failed: $Message`nMismatch at index $i`nExpected: $($expectedSorted -join ', ')`nActual: $($actualSorted -join ', ')"
        }
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

# Load the golden fixture
$fixturePath = Join-Path $PSScriptRoot "fixtures/applemusicarr-schema-fields.json"
$fixture = Get-Content $fixturePath -Raw | ConvertFrom-Json

Write-Host "`n=== AppleMusicarr Schema Field Tests ===" -ForegroundColor Cyan

# ============================================================================
# Golden Fixture Structure Tests
# ============================================================================
Write-Host "`n--- Golden Fixture Structure Tests ---" -ForegroundColor Yellow

Run-Test "Fixture has expectedFields" {
    if ($null -eq $fixture.expectedFields) {
        throw "Fixture missing expectedFields"
    }
}

Run-Test "Fixture has importList section" {
    if ($null -eq $fixture.expectedFields.importList) {
        throw "Fixture missing expectedFields.importList"
    }
}

Run-Test "ImportList has requiredCredentials" {
    $creds = $fixture.expectedFields.importList.requiredCredentials
    if ($null -eq $creds -or $creds.Count -eq 0) {
        throw "Fixture missing importList.requiredCredentials"
    }
}

Run-Test "Fixture has 4 required credential fields" {
    $creds = @($fixture.expectedFields.importList.requiredCredentials)
    Assert-Equal 4 $creds.Count "Should have 4 required credential fields"
}

# ============================================================================
# Required Credential Field Tests
# ============================================================================
Write-Host "`n--- Required Credential Field Tests ---" -ForegroundColor Yellow

$requiredFields = @($fixture.expectedFields.importList.requiredCredentials)

Run-Test "teamId is required" {
    Assert-Contains $requiredFields "teamId" "teamId should be in required fields"
}

Run-Test "keyId is required" {
    Assert-Contains $requiredFields "keyId" "keyId should be in required fields"
}

Run-Test "privateKey is required" {
    Assert-Contains $requiredFields "privateKey" "privateKey should be in required fields"
}

Run-Test "musicUserToken is required" {
    Assert-Contains $requiredFields "musicUserToken" "musicUserToken should be in required fields"
}

# ============================================================================
# Sensitive Field Classification Tests
# ============================================================================
Write-Host "`n--- Sensitive Field Classification Tests ---" -ForegroundColor Yellow

$sensitiveFields = @($fixture.expectedFields.importList.sensitiveFields)
$identifierFields = @($fixture.expectedFields.importList.identifierFields)

Run-Test "privateKey is sensitive" {
    Assert-Contains $sensitiveFields "privateKey" "privateKey should be in sensitive fields"
}

Run-Test "musicUserToken is sensitive" {
    Assert-Contains $sensitiveFields "musicUserToken" "musicUserToken should be in sensitive fields"
}

Run-Test "teamId is identifier (NOT sensitive)" {
    Assert-NotContains $sensitiveFields "teamId" "teamId should NOT be in sensitive fields"
    Assert-Contains $identifierFields "teamId" "teamId should be in identifier fields"
}

Run-Test "keyId is identifier (NOT sensitive)" {
    Assert-NotContains $sensitiveFields "keyId" "keyId should NOT be in sensitive fields"
    Assert-Contains $identifierFields "keyId" "keyId should be in identifier fields"
}

Run-Test "Sensitive and identifier fields are disjoint" {
    foreach ($field in $sensitiveFields) {
        if ($field -in $identifierFields) {
            throw "$field appears in both sensitive and identifier fields"
        }
    }
}

Run-Test "All required fields are classified" {
    $allClassified = @($sensitiveFields) + @($identifierFields)
    foreach ($field in $requiredFields) {
        if ($field -notin $allClassified) {
            throw "Required field '$field' is not classified as sensitive or identifier"
        }
    }
}

# ============================================================================
# Env Var Mapping Tests
# ============================================================================
Write-Host "`n--- Env Var Mapping Tests ---" -ForegroundColor Yellow

Run-Test "APPLEMUSICARR_TEAM_ID maps to teamId" {
    Assert-Equal "teamId" $fixture.envVarMapping.APPLEMUSICARR_TEAM_ID
}

Run-Test "APPLEMUSICARR_KEY_ID maps to keyId" {
    Assert-Equal "keyId" $fixture.envVarMapping.APPLEMUSICARR_KEY_ID
}

Run-Test "APPLEMUSICARR_PRIVATE_KEY_B64 maps to privateKey" {
    Assert-Equal "privateKey" $fixture.envVarMapping.APPLEMUSICARR_PRIVATE_KEY_B64
}

Run-Test "APPLEMUSICARR_MUSIC_USER_TOKEN maps to musicUserToken" {
    Assert-Equal "musicUserToken" $fixture.envVarMapping.APPLEMUSICARR_MUSIC_USER_TOKEN
}

# ============================================================================
# C# Property Mapping Tests
# ============================================================================
Write-Host "`n--- C# Property Mapping Tests ---" -ForegroundColor Yellow

Run-Test "TeamId property maps to teamId schema field" {
    Assert-Equal "teamId" $fixture.csharpPropertyMapping.TeamId
}

Run-Test "KeyId property maps to keyId schema field" {
    Assert-Equal "keyId" $fixture.csharpPropertyMapping.KeyId
}

Run-Test "PrivateKey property maps to privateKey schema field" {
    Assert-Equal "privateKey" $fixture.csharpPropertyMapping.PrivateKey
}

Run-Test "MusicUserToken property maps to musicUserToken schema field" {
    Assert-Equal "musicUserToken" $fixture.csharpPropertyMapping.MusicUserToken
}

# ============================================================================
# E2E Runner Config Validation (if available)
# ============================================================================
Write-Host "`n--- E2E Runner Config Validation ---" -ForegroundColor Yellow

# Try to find the E2E runner script and extract AppleMusicarr config
$runnerPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "scripts/e2e-runner.ps1"

# Check if runner exists and has AppleMusicarr support
$runnerContent = $null
if (Test-Path $runnerPath) {
    $runnerContent = Get-Content $runnerPath -Raw
}

Run-Test "E2E runner AppleMusicarr config (when merged)" {
    if ($null -eq $runnerContent) {
        Write-Host "    (Skipped - e2e-runner.ps1 not found at expected path)" -ForegroundColor Yellow
        return
    }

    # AppleMusicarr support is added in feat/applemusicarr-configure-support branch
    # This test validates the config is correct when merged
    if ($runnerContent -match "'AppleMusicarr'\s*=\s*@\{") {
        # Config exists - validate the field names match our fixture
        if ($runnerContent -match "ImportListCredentialAllOfFieldNames\s*=\s*@\([^)]+\)") {
            $match = $Matches[0]
            foreach ($field in $requiredFields) {
                if ($match -notmatch "`"$field`"") {
                    throw "E2E runner missing required field: $field in ImportListCredentialAllOfFieldNames"
                }
            }
            Write-Host "    (AppleMusicarr config validated)" -ForegroundColor Green
        }
    } else {
        # Config not yet merged - this is expected until PR #218 merges
        Write-Host "    (AppleMusicarr not in main yet - waiting for PR #218)" -ForegroundColor Yellow
    }
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
