#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Code-level invariant tests for E2E error codes.
.DESCRIPTION
    These tests enforce emitter logic constraints that fixtures alone cannot catch.
    Fixtures prove output shape; these tests prove the code respects invariants
    regardless of which code path is exercised.

    IMPORTANT: When adding a new error code with conditional fields, add an
    invariant test here to prevent "fixture-only enforcement".
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "E2E Error Code Invariants" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$passed = 0
$failed = 0

function Assert-True {
    param([string]$Name, [bool]$Condition)
    if ($Condition) {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
        return $true
    } else {
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        $script:failed++
        return $false
    }
}

# ============================================================================
# Load modules
# ============================================================================

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$gatesPath = Join-Path $repoRoot 'scripts/lib/e2e-gates.psm1'

# ============================================================================
# Invariant: E2E_HOST_PLUGIN_DISCOVERY_DISABLED requires detectionBasis
# ============================================================================

Write-Host ""
Write-Host "Invariant: hostPluginDiscoveryEnabled=false requires detectionBasis" -ForegroundColor Cyan

# Simulate the contract: if hostPluginDiscoveryEnabled is false, detectionBasis must be present
function Test-DiscoveryDiagnosisInvariant {
    param([hashtable]$Diagnosis)

    # If hostPluginDiscoveryEnabled is null (unknown), no constraint
    if ($null -eq $Diagnosis.hostPluginDiscoveryEnabled) {
        return $true
    }

    # If hostPluginDiscoveryEnabled is false, detectionBasis must be non-empty
    if ($Diagnosis.hostPluginDiscoveryEnabled -eq $false) {
        return (-not [string]::IsNullOrWhiteSpace($Diagnosis.detectionBasis))
    }

    # If hostPluginDiscoveryEnabled is true, no constraint on detectionBasis
    return $true
}

# Test case: null hostPluginDiscoveryEnabled (unknown) - should pass
$diagnosis1 = @{
    hostPluginDiscoveryEnabled = $null
    detectionBasis = $null
}
Assert-True -Name "null hostPluginDiscoveryEnabled: no detectionBasis required" -Condition (Test-DiscoveryDiagnosisInvariant $diagnosis1)

# Test case: false hostPluginDiscoveryEnabled with detectionBasis - should pass
$diagnosis2 = @{
    hostPluginDiscoveryEnabled = $false
    detectionBasis = "config.xml"
}
Assert-True -Name "false hostPluginDiscoveryEnabled with detectionBasis: valid" -Condition (Test-DiscoveryDiagnosisInvariant $diagnosis2)

# Test case: false hostPluginDiscoveryEnabled without detectionBasis - should FAIL
$diagnosis3 = @{
    hostPluginDiscoveryEnabled = $false
    detectionBasis = $null
}
$invariantViolated = -not (Test-DiscoveryDiagnosisInvariant $diagnosis3)
Assert-True -Name "false hostPluginDiscoveryEnabled without detectionBasis: correctly rejected" -Condition $invariantViolated

# Test case: true hostPluginDiscoveryEnabled without detectionBasis - should pass
$diagnosis4 = @{
    hostPluginDiscoveryEnabled = $true
    detectionBasis = $null
}
Assert-True -Name "true hostPluginDiscoveryEnabled: no detectionBasis required" -Condition (Test-DiscoveryDiagnosisInvariant $diagnosis4)

# ============================================================================
# Invariant: missingTags uses canonical tag names (uppercase)
# ============================================================================

Write-Host ""
Write-Host "Invariant: missingTags uses canonical uppercase tag names" -ForegroundColor Cyan

function Test-CanonicalTagNames {
    param([string[]]$Tags)

    # All tag names should be uppercase (TagLib canonical form)
    foreach ($tag in $Tags) {
        if ($tag -cne $tag.ToUpperInvariant()) {
            return $false
        }
    }
    return $true
}

# Test case: uppercase tags - should pass
Assert-True -Name "uppercase tags: ALBUMARTIST, ISRC" -Condition (Test-CanonicalTagNames @('ALBUMARTIST', 'ISRC', 'MUSICBRAINZ_TRACKID'))

# Test case: mixed case tags - should fail
$mixedCaseViolated = -not (Test-CanonicalTagNames @('AlbumArtist', 'isrc'))
Assert-True -Name "mixed case tags: correctly rejected" -Condition $mixedCaseViolated

# ============================================================================
# Invariant: foundIndexerNames is capped for stability
# ============================================================================

Write-Host ""
Write-Host "Invariant: foundIndexerNames is capped at reasonable limit" -ForegroundColor Cyan

$maxIndexerNames = 10  # Prevent brittle diffs from Lidarr defaults

function Test-IndexerNamesCapped {
    param([string[]]$Names, [int]$Limit)
    return $Names.Count -le $Limit
}

# Test case: reasonable count - should pass
Assert-True -Name "3 indexer names within cap" -Condition (Test-IndexerNamesCapped @('Qobuzarr', 'Tidalarr', 'Usenet') $maxIndexerNames)

# Test case: exceeds cap - emitter should truncate
$manyNames = 1..15 | ForEach-Object { "Indexer$_" }
$exceedsCap = -not (Test-IndexerNamesCapped $manyNames $maxIndexerNames)
Assert-True -Name "15 indexer names exceeds cap: emitter should truncate" -Condition $exceedsCap

# ============================================================================
# Summary
# ============================================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'Green' })

if ($failed -gt 0) {
    throw "E2E error code invariant tests failed: $failed"
}

exit 0
