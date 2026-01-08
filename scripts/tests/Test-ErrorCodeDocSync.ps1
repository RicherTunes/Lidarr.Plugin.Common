#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Doc-sync tripwire: ensures every canonical E2E error code is documented.
.DESCRIPTION
    This test prevents "new code, no doc" drift by asserting:
    1. Every error code in e2e-error-codes.psm1 (canonical list) exists in docs/E2E_ERROR_CODES.md
    2. The doc table doesn't contain codes not in the canonical list (stale docs)

    IMPORTANT: When adding a new E2E_* error code:
    1. Add the code to scripts/lib/e2e-error-codes.psm1 (canonical source of truth)
    2. Add a row to docs/E2E_ERROR_CODES.md
    3. This test will fail CI if either step is skipped
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "E2E Error Code Doc-Sync Tripwire" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$failed = 0
$passed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "  [PASS] $Message" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        $script:failed++
    }
}

# ============================================================================
# Paths
# ============================================================================

$scriptRoot = $PSScriptRoot
$repoRoot = (Get-Item (Join-Path $scriptRoot "../..")).FullName
$canonicalModulePath = Join-Path $repoRoot "scripts/lib/e2e-error-codes.psm1"
$docsPath = Join-Path $repoRoot "docs/E2E_ERROR_CODES.md"

# ============================================================================
# Load canonical error codes
# ============================================================================

Write-Host ""
Write-Host "Loading canonical error codes from e2e-error-codes.psm1..." -ForegroundColor DarkGray

$canonicalCodes = @()
if (Test-Path $canonicalModulePath) {
    Import-Module $canonicalModulePath -Force
    $canonicalCodes = Get-E2EErrorCodes
    Write-Host "  Found $($canonicalCodes.Count) canonical codes" -ForegroundColor DarkGray
} else {
    Write-Host "  [FAIL] Canonical module not found: $canonicalModulePath" -ForegroundColor Red
    $failed++
}

# ============================================================================
# Extract error codes from docs table (Error Code Reference section only)
# ============================================================================

Write-Host "Extracting codes from docs/E2E_ERROR_CODES.md table..." -ForegroundColor DarkGray

$documentedCodes = @()
if (Test-Path $docsPath) {
    $docsContent = Get-Content $docsPath -Raw

    # Extract only the Error Code Reference table section (between ## Error Code Reference and ## Related)
    $tableSection = $null
    if ($docsContent -match '(?s)## Error Code Reference\s*\n(.+?)(?=\n## |$)') {
        $tableSection = $Matches[1]
    }

    if ($tableSection) {
        # Match table rows: | `E2E_FOO_BAR` |
        # Use single quotes to avoid PowerShell backtick issues
        $pattern = '\|\s*`(E2E_[A-Z0-9_]+)`\s*\|'
        $docsMatches = [regex]::Matches($tableSection, $pattern)
        $documentedCodes = $docsMatches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        Write-Host "  Found $($documentedCodes.Count) codes in docs table" -ForegroundColor DarkGray
    } else {
        Write-Host "  [WARN] Could not find Error Code Reference table section" -ForegroundColor Yellow
    }
} else {
    Write-Host "  [FAIL] Documentation not found: $docsPath" -ForegroundColor Red
    $failed++
}

# ============================================================================
# Verify all canonical codes are documented
# ============================================================================

Write-Host ""
Write-Host "Verifying all canonical codes are documented..." -ForegroundColor Cyan

$undocumented = @()
foreach ($code in $canonicalCodes) {
    if ($code -notin $documentedCodes) {
        $undocumented += $code
        Write-Host "  [FAIL] $code - NOT DOCUMENTED" -ForegroundColor Red
        $failed++
    } else {
        Write-Host "  [PASS] $code" -ForegroundColor Green
        $passed++
    }
}

# ============================================================================
# Verify no stale documented codes (docs without canonical definition)
# ============================================================================

Write-Host ""
Write-Host "Checking for stale documented codes..." -ForegroundColor Cyan

$stale = @()
foreach ($code in $documentedCodes) {
    if ($code -notin $canonicalCodes) {
        $stale += $code
        Write-Host "  [FAIL] $code - documented but NOT in canonical list (stale or missing from e2e-error-codes.psm1)" -ForegroundColor Red
        $failed++
    }
}

if ($stale.Count -eq 0) {
    Write-Host "  [OK] No stale codes found" -ForegroundColor Green
}

# ============================================================================
# Summary
# ============================================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Canonical codes: $($canonicalCodes.Count)" -ForegroundColor Cyan
Write-Host "Documented codes: $($documentedCodes.Count)" -ForegroundColor Cyan
Write-Host "Undocumented:     $($undocumented.Count)" -ForegroundColor $(if ($undocumented.Count -gt 0) { 'Red' } else { 'Green' })
Write-Host "Stale:            $($stale.Count)" -ForegroundColor $(if ($stale.Count -gt 0) { 'Red' } else { 'Green' })
Write-Host ""
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'Green' })

if ($undocumented.Count -gt 0) {
    Write-Host ""
    Write-Host "ACTION REQUIRED:" -ForegroundColor Red
    Write-Host "Add the following codes to docs/E2E_ERROR_CODES.md:" -ForegroundColor Red
    foreach ($code in $undocumented) {
        Write-Host "  - $code" -ForegroundColor Red
    }
}

if ($stale.Count -gt 0) {
    Write-Host ""
    Write-Host "ACTION REQUIRED:" -ForegroundColor Red
    Write-Host "Add the following codes to scripts/lib/e2e-error-codes.psm1 OR remove from docs:" -ForegroundColor Red
    foreach ($code in $stale) {
        Write-Host "  - $code" -ForegroundColor Red
    }
}

# ============================================================================
# Verify golden manifest error codes are in canonical list
# ============================================================================

Write-Host ""
Write-Host "Verifying golden manifest error codes are canonical..." -ForegroundColor Cyan

$fixturesDir = Join-Path $repoRoot "scripts/tests/fixtures/golden-manifests"
if (Test-Path $fixturesDir) {
    $goldenCodes = @()
    Get-ChildItem $fixturesDir -Filter '*.json' | ForEach-Object {
        $content = Get-Content $_.FullName -Raw | ConvertFrom-Json
        $content.results | Where-Object { $null -ne $_.errorCode } | ForEach-Object {
            $goldenCodes += $_.errorCode
        }
    }
    $goldenCodes = $goldenCodes | Sort-Object -Unique

    $missingFromCanonical = @()
    foreach ($code in $goldenCodes) {
        if ($code -notin $canonicalCodes) {
            $missingFromCanonical += $code
            Write-Host "  [FAIL] Golden fixture uses $code but NOT in canonical list" -ForegroundColor Red
            $failed++
        } else {
            Write-Host "  [PASS] $code (golden fixture)" -ForegroundColor Green
            $passed++
        }
    }

    if ($missingFromCanonical.Count -eq 0) {
        Write-Host "  [OK] All golden manifest codes are canonical" -ForegroundColor Green
    }
} else {
    Write-Host "  [SKIP] Fixtures dir not found" -ForegroundColor Yellow
}

if ($failed -gt 0) {
    throw "E2E error code doc-sync failed: $failed issues found"
}

exit 0
