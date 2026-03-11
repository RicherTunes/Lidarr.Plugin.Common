#!/usr/bin/env pwsh
# Test that DOC_VERSION markers exist in key contract documents
# Prevents accidental removal during cleanup/refactoring

$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    return (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "DOC_VERSION Marker Audit" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

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

$repoRoot = Get-RepoRoot

# ============================================================================
# TRACK_IDENTITY_PARITY.md DOC_VERSION
# ============================================================================
Write-Host ""
Write-Host "Test Group: TRACK_IDENTITY_PARITY.md" -ForegroundColor Yellow

$parityDocPath = Join-Path $repoRoot "docs" "TRACK_IDENTITY_PARITY.md"
$parityDocExists = Test-Path $parityDocPath
Test-Assertion $parityDocExists "TRACK_IDENTITY_PARITY.md exists"

if ($parityDocExists) {
    $content = Get-Content -Path $parityDocPath -Raw

    # DOC_VERSION marker must exist
    $hasDocVersion = $content -match 'DOC_VERSION:'
    Test-Assertion $hasDocVersion "DOC_VERSION marker present"

    # DOC_VERSION format: DOC_VERSION: YYYY-MM-DD-vN
    $validFormat = $content -match '<!-- DOC_VERSION: \d{4}-\d{2}-\d{2}-v\d+ -->'
    Test-Assertion $validFormat "DOC_VERSION matches expected format (YYYY-MM-DD-vN)"

    # Extract and display current version
    if ($content -match '<!-- DOC_VERSION: (\d{4}-\d{2}-\d{2}-v\d+) -->') {
        $currentVersion = $Matches[1]
        Write-Host "    Current version: $currentVersion" -ForegroundColor DarkGray
    }

    # Tier semantics section must exist (critical contract section)
    $hasTierSemantics = $content -match '## Tier Semantics'
    Test-Assertion $hasTierSemantics "Tier Semantics section exists"

    # Tier 1/2/3 must be defined
    $hasTier1 = $content -match '\*\*Tier 1\*\*'
    $hasTier2 = $content -match '\*\*Tier 2\*\*'
    $hasTier3 = $content -match '\*\*Tier 3\*\*'
    Test-Assertion ($hasTier1 -and $hasTier2 -and $hasTier3) "All three tiers defined"
}

# ============================================================================
# E2E_ERROR_CODES.md Structure
# ============================================================================
Write-Host ""
Write-Host "Test Group: E2E_ERROR_CODES.md" -ForegroundColor Yellow

$errorCodesPath = Join-Path $repoRoot "docs" "E2E_ERROR_CODES.md"
$errorCodesExists = Test-Path $errorCodesPath
Test-Assertion $errorCodesExists "E2E_ERROR_CODES.md exists"

if ($errorCodesExists) {
    $content = Get-Content -Path $errorCodesPath -Raw

    # Structured Details Contract section must exist
    $hasStructuredDetails = $content -match '## Structured Details Contract'
    Test-Assertion $hasStructuredDetails "Structured Details Contract section exists"

    # E2E_METADATA_MISSING must have readError field documented
    $hasReadError = $content -match '\| `readError` \|'
    Test-Assertion $hasReadError "E2E_METADATA_MISSING.readError field documented"

    # E2E_METADATA_MISSING must have tagReadTool field documented
    $hasTagReadTool = $content -match '\| `tagReadTool` \|'
    Test-Assertion $hasTagReadTool "E2E_METADATA_MISSING.tagReadTool field documented"
}

# ============================================================================
# Summary
# ============================================================================
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Audit Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'Green' })

if ($failed -gt 0) { exit 1 }
exit 0
