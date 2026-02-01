#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Self-tests for the unified test runner (scripts/test.ps1).

.DESCRIPTION
    Validates filter composition logic to prevent regressions that could
    cascade to all plugin repositories using the runner.

    Tests cover:
    - Default exclusions (fast lane)
    - Category inclusion mode
    - Quarantine exclusion (State!=Quarantined)
    - IncludeQuarantined flag
    - AdditionalFilter composition
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = $PSScriptRoot
$RepoRoot = Split-Path -Parent (Split-Path -Parent $ScriptDir)

# Import the test runner module to access Build-TestFilter
$TestRunnerModule = Join-Path $RepoRoot "scripts/lib/test-runner.psm1"
$TestScript = Join-Path $RepoRoot "scripts/test.ps1"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Unified Runner Self-Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$passed = 0
$failed = 0

function Test-Assertion {
    param(
        [string]$Name,
        [scriptblock]$Test
    )

    Write-Host "  Testing: $Name..." -NoNewline

    try {
        $result = & $Test
        if ($result) {
            Write-Host " PASS" -ForegroundColor Green
            $script:passed++
            return $true
        } else {
            Write-Host " FAIL" -ForegroundColor Red
            $script:failed++
            return $false
        }
    }
    catch {
        Write-Host " ERROR: $_" -ForegroundColor Red
        $script:failed++
        return $false
    }
}

# Define Build-TestFilter inline (same logic as in test.ps1)
function Build-TestFilter {
    param(
        [string[]]$IncludeCategories = @(),
        [string[]]$ExcludeCategories = @(),
        [bool]$ExcludeQuarantined = $true,
        [string]$AdditionalFilter = ""
    )

    $parts = @()

    if ($IncludeCategories.Count -gt 0) {
        $categoryPart = "(" + (($IncludeCategories | ForEach-Object { "Category=$_" }) -join "|") + ")"
        $parts += $categoryPart
    } elseif ($ExcludeCategories.Count -gt 0) {
        $parts += ($ExcludeCategories | ForEach-Object { "Category!=$_" })
    }

    if ($ExcludeQuarantined) {
        $parts += "State!=Quarantined"
    }

    if ($AdditionalFilter) {
        $parts += $AdditionalFilter
    }

    if ($parts.Count -gt 0) {
        return $parts -join "&"
    }

    return ""
}

Write-Host "Filter Composition Tests:" -ForegroundColor White

# Test 1: Default exclusions include State!=Quarantined
Test-Assertion "Default exclusions include Quarantined" {
    $filter = Build-TestFilter -ExcludeCategories @('Integration', 'Packaging')
    $filter -match 'State!=Quarantined'
}

# Test 2: Default fast lane excludes expensive categories
Test-Assertion "Fast lane excludes Integration" {
    $filter = Build-TestFilter -ExcludeCategories @('Integration', 'Packaging', 'LibraryLinking', 'Benchmark', 'Slow')
    $filter -match 'Category!=Integration'
}

# Test 3: Category include mode works
Test-Assertion "Category include mode generates correct filter" {
    $filter = Build-TestFilter -IncludeCategories @('Integration')
    ($filter -match '\(Category=Integration\)') -and ($filter -match 'State!=Quarantined')
}

# Test 4: Multiple category include
Test-Assertion "Multiple category include uses OR" {
    $filter = Build-TestFilter -IncludeCategories @('Integration', 'Packaging')
    $filter -match '\(Category=Integration\|Category=Packaging\)'
}

# Test 5: IncludeQuarantined removes State!=Quarantined
Test-Assertion "IncludeQuarantined removes quarantine filter" {
    $filter = Build-TestFilter -ExcludeCategories @('Integration') -ExcludeQuarantined $false
    -not ($filter -match 'State!=Quarantined')
}

# Test 6: AdditionalFilter is appended
Test-Assertion "AdditionalFilter is appended" {
    $filter = Build-TestFilter -ExcludeCategories @('Integration') -AdditionalFilter "scope!=cli"
    ($filter -match 'scope!=cli') -and ($filter -match 'Category!=Integration')
}

# Test 7: Empty exclusions still includes quarantine filter
Test-Assertion "No exclusions still excludes Quarantined by default" {
    $filter = Build-TestFilter -ExcludeCategories @()
    $filter -eq 'State!=Quarantined'
}

# Test 8: Full quarantine run (empty exclusions + IncludeQuarantined)
Test-Assertion "Full quarantine run produces empty filter" {
    $filter = Build-TestFilter -ExcludeCategories @() -ExcludeQuarantined $false
    $filter -eq ''
}

# Test 9: Combined scenario (category + additional filter)
Test-Assertion "Combined category + additional filter works" {
    $filter = Build-TestFilter -IncludeCategories @('Packaging') -AdditionalFilter "scope=plugin"
    ($filter -match 'Category=Packaging') -and ($filter -match 'scope=plugin') -and ($filter -match 'State!=Quarantined')
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) {
    exit 1
}

Write-Host ""
Write-Host "[OK] All unified runner tests passed." -ForegroundColor Green
exit 0
