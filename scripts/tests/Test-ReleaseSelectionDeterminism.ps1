#!/usr/bin/env pwsh
# Tests that release selection is deterministic regardless of input order

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Release Selection Determinism Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$passed = 0
$failed = 0

function Assert-True {
    param(
        [Parameter(Mandatory)] [bool]$Condition,
        [Parameter(Mandatory)] [string]$Description
    )
    if ($Condition) {
        Write-Host "  [PASS] $Description" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  [FAIL] $Description" -ForegroundColor Red
        $script:failed++
    }
}

# Import the shared module
$scriptDir = Split-Path $PSScriptRoot -Parent
$modulePath = Join-Path $scriptDir "lib/e2e-release-selection.psm1"

Write-Host "`nTest 0: Module exists and imports" -ForegroundColor Yellow
Assert-True -Condition (Test-Path -LiteralPath $modulePath) -Description "e2e-release-selection.psm1 exists"
Import-Module $modulePath -Force

$exportedFunctions = (Get-Module -Name 'e2e-release-selection').ExportedFunctions.Keys
Assert-True -Condition ($exportedFunctions -contains 'Select-DeterministicRelease') -Description "Select-DeterministicRelease is exported"

function Shuffle-Array {
    param([array]$Array)
    return $Array | Get-Random -Count $Array.Count
}

# Test data: releases with same titles but different guids
$testReleases = @(
    [PSCustomObject]@{ title = "Album A"; guid = "guid-3"; size = 100 }
    [PSCustomObject]@{ title = "Album A"; guid = "guid-1"; size = 200 }
    [PSCustomObject]@{ title = "Album A"; guid = "guid-2"; size = 150 }
    [PSCustomObject]@{ title = "Album B"; guid = "guid-4"; size = 300 }
    [PSCustomObject]@{ title = "album a"; guid = "guid-5"; size = 50 }  # Lowercase - should sort same as Album A
)

Write-Host "`nTest 1: Same selection across multiple shuffles" -ForegroundColor Yellow
$selections = @()
for ($i = 0; $i -lt 10; $i++) {
    $shuffled = Shuffle-Array -Array $testReleases
    $selected = Select-DeterministicRelease -Releases $shuffled
    $selections += $selected.guid
}
$uniqueSelections = @($selections | Select-Object -Unique)
Assert-True -Condition ($uniqueSelections.Count -eq 1) -Description "All 10 shuffled runs select same release (got $($uniqueSelections.Count) unique)"

Write-Host "`nTest 2: Selects alphabetically first title (case-insensitive)" -ForegroundColor Yellow
$selected = Select-DeterministicRelease -Releases $testReleases
# "Album A" and "album a" should both sort to the top (case-insensitive)
Assert-True -Condition ($selected.title -match '^[Aa]lbum A$') -Description "Selected title is 'Album A' (got '$($selected.title)')"

Write-Host "`nTest 3: Tie-break by guid when titles match" -ForegroundColor Yellow
# After title tie, should pick guid-1 (alphabetically first among same-title releases)
Assert-True -Condition ($selected.guid -eq 'guid-1') -Description "Selected guid is 'guid-1' (got '$($selected.guid)')"

Write-Host "`nTest 4: Handles null/missing properties gracefully" -ForegroundColor Yellow
$releasesWithNulls = @(
    [PSCustomObject]@{ title = $null; guid = "guid-a"; size = 100 }
    [PSCustomObject]@{ title = "Album"; guid = $null; size = 200 }
    [PSCustomObject]@{ title = "Album"; guid = "guid-b"; size = $null }
)
$selectedFromNulls = $null
try {
    $selectedFromNulls = Select-DeterministicRelease -Releases $releasesWithNulls
    Assert-True -Condition ($null -ne $selectedFromNulls) -Description "Handles null properties without throwing"
} catch {
    Assert-True -Condition $false -Description "Handles null properties without throwing (threw: $_)"
}

Write-Host "`nTest 5: Empty title sorts before non-empty (null-coalesce)" -ForegroundColor Yellow
# null title becomes '' which sorts before 'Album'
if ($selectedFromNulls) {
    Assert-True -Condition ($null -eq $selectedFromNulls.title -or $selectedFromNulls.title -eq '') -Description "Null/empty title sorts first"
}

Write-Host "`nTest 6: Size is tertiary tie-breaker (descending by default)" -ForegroundColor Yellow
$sameTitleGuid = @(
    [PSCustomObject]@{ title = "Same"; guid = "same-guid"; size = 100 }
    [PSCustomObject]@{ title = "Same"; guid = "same-guid"; size = 300 }
    [PSCustomObject]@{ title = "Same"; guid = "same-guid"; size = 200 }
)
$selectedBySize = Select-DeterministicRelease -Releases $sameTitleGuid
Assert-True -Condition ($selectedBySize.size -eq 300) -Description "Largest size wins as tertiary tie-breaker (got $($selectedBySize.size))"

Write-Host "`nTest 7: ReturnSelectionBasis provides manifest data" -ForegroundColor Yellow
$result = Select-DeterministicRelease -Releases $testReleases -ReturnSelectionBasis
Assert-True -Condition ($null -ne $result.release) -Description "ReturnSelectionBasis includes release"
Assert-True -Condition ($null -ne $result.selectionBasis) -Description "ReturnSelectionBasis includes selectionBasis"
Assert-True -Condition ($result.selectionBasis.candidateCount -eq 5) -Description "selectionBasis.candidateCount is correct (got $($result.selectionBasis.candidateCount))"
Assert-True -Condition ($result.selectionBasis.sortKeys -is [array]) -Description "selectionBasis.sortKeys is array"
Assert-True -Condition ($result.selectionBasis.sortKeys -contains 'intrinsicHash:asc') -Description "sortKeys includes intrinsicHash"
Assert-True -Condition ($null -ne $result.selectionBasis.selectedGuidHash) -Description "selectionBasis includes selectedGuidHash (not raw guid)"
Assert-True -Condition ($null -ne $result.selectionBasis.selectedIntrinsicHash) -Description "selectionBasis includes selectedIntrinsicHash"

Write-Host "`nTest 8: Empty releases returns null" -ForegroundColor Yellow
$emptyResult = Select-DeterministicRelease -Releases @()
Assert-True -Condition ($null -eq $emptyResult) -Description "Empty releases returns null"

$emptyWithBasis = Select-DeterministicRelease -Releases @() -ReturnSelectionBasis
Assert-True -Condition ($null -eq $emptyWithBasis.release) -Description "Empty releases with basis returns null release"
Assert-True -Condition ($emptyWithBasis.selectionBasis.error -eq 'no_releases') -Description "Empty releases with basis sets error"

Write-Host "`nTest 9: Null/empty guid determinism (50 shuffles)" -ForegroundColor Yellow
# Mix of null, empty, and valid guids - all with same title
# After guid normalization: null→"", ""→"", "valid-guid"→"VALID-GUID"
# Empty strings sort before "VALID-GUID", so the 4 with empty guid compete
# Among those 4, size (desc) decides: 300 > 250 > 150 > 100
$mixedGuidReleases = @(
    [PSCustomObject]@{ title = "Same"; guid = $null; size = 100 }
    [PSCustomObject]@{ title = "Same"; guid = ""; size = 150 }
    [PSCustomObject]@{ title = "Same"; guid = "valid-guid"; size = 200 }
    [PSCustomObject]@{ title = "Same"; guid = $null; size = 250 }
    [PSCustomObject]@{ title = "Same"; guid = ""; size = 300 }     # Should win (empty guid sorts first, largest size)
)
$nullGuidSelections = @()
for ($i = 0; $i -lt 50; $i++) {
    $shuffled = Shuffle-Array -Array $mixedGuidReleases
    $sel = Select-DeterministicRelease -Releases $shuffled -ReturnSelectionBasis
    # Track by actual release properties, not originalIndex (which changes with shuffle)
    $nullGuidSelections += "$($sel.release.guid ?? 'null'):$($sel.release.size)"
}
$uniqueNullGuidSelections = @($nullGuidSelections | Select-Object -Unique)
Assert-True -Condition ($uniqueNullGuidSelections.Count -eq 1) -Description "50 shuffles with null/empty guid select same release (got $($uniqueNullGuidSelections.Count) unique)"

# Verify the winner has empty guid and size 300
$mixedResult = Select-DeterministicRelease -Releases $mixedGuidReleases -ReturnSelectionBasis
Assert-True -Condition ($mixedResult.release.size -eq 300) -Description "Largest size with empty guid wins (got size $($mixedResult.release.size))"
Assert-True -Condition ([string]::IsNullOrEmpty($mixedResult.release.guid)) -Description "Winner has null/empty guid"

Write-Host "`nTest 10: SelectionBasis contains no sensitive fields" -ForegroundColor Yellow
$sensitivePatterns = @('http:', 'https:', 'token', 'apikey', 'api_key', 'password', 'secret')
$basisJson = $result.selectionBasis | ConvertTo-Json -Depth 5
$hasSensitive = $false
foreach ($pattern in $sensitivePatterns) {
    if ($basisJson -match $pattern) {
        $hasSensitive = $true
        Write-Host "    Found sensitive pattern: $pattern" -ForegroundColor Red
    }
}
Assert-True -Condition (-not $hasSensitive) -Description "selectionBasis contains no sensitive patterns"

Write-Host "`nTest 11: SizeAscending mode selects smallest" -ForegroundColor Yellow
# All have same title and guid, so size is the deciding factor
$sizeTestReleases = @(
    [PSCustomObject]@{ title = "Album"; guid = "same"; size = 500 }
    [PSCustomObject]@{ title = "Album"; guid = "same"; size = 100 }
    [PSCustomObject]@{ title = "Album"; guid = "same"; size = 300 }
)
$smallestFirst = Select-DeterministicRelease -Releases $sizeTestReleases -SizeAscending
Assert-True -Condition ($smallestFirst.size -eq 100) -Description "SizeAscending selects smallest (got $($smallestFirst.size))"

$largestFirst = Select-DeterministicRelease -Releases $sizeTestReleases
Assert-True -Condition ($largestFirst.size -eq 500) -Description "Default (desc) selects largest (got $($largestFirst.size))"

Write-Host "`nTest 12: SizeAscending puts null size last" -ForegroundColor Yellow
# All have same title and guid, so size is the deciding factor
# In SizeAscending mode, null sizes become MaxValue (sort last)
$nullSizeReleases = @(
    [PSCustomObject]@{ title = "Album"; guid = "same"; size = $null }
    [PSCustomObject]@{ title = "Album"; guid = "same"; size = 100 }
    [PSCustomObject]@{ title = "Album"; guid = "same"; size = 50 }
)
$ascWithNull = Select-DeterministicRelease -Releases $nullSizeReleases -SizeAscending
Assert-True -Condition ($ascWithNull.size -eq 50) -Description "SizeAscending with null size selects smallest non-null (got $($ascWithNull.size))"

Write-Host "`nTest 13: originalIndex ensures stability" -ForegroundColor Yellow
# All keys identical - only originalIndex differs
$identicalReleases = @(
    [PSCustomObject]@{ title = "Same"; guid = "same"; size = 100 }
    [PSCustomObject]@{ title = "Same"; guid = "same"; size = 100 }
    [PSCustomObject]@{ title = "Same"; guid = "same"; size = 100 }
)
$stabilitySelections = @()
for ($i = 0; $i -lt 20; $i++) {
    $shuffled = Shuffle-Array -Array $identicalReleases
    $sel = Select-DeterministicRelease -Releases $shuffled -ReturnSelectionBasis
    $stabilitySelections += $sel.selectionBasis.selectedOriginalIndex
}
# After shuffling, selectedOriginalIndex should always be 0 (first in shuffled array)
$uniqueStability = @($stabilitySelections | Select-Object -Unique)
Assert-True -Condition ($uniqueStability.Count -eq 1 -and $uniqueStability[0] -eq 0) -Description "originalIndex ensures stable selection (always picks index 0)"

$stableResult = Select-DeterministicRelease -Releases $identicalReleases -ReturnSelectionBasis
Assert-True -Condition ($stableResult.selectionBasis.tieBreaker -eq 'originalIndex') -Description "tieBreaker reports 'originalIndex' for identical releases"

Write-Host "`nTest 14: Different initial order selects same item (intrinsic key)" -ForegroundColor Yellow
# Critical test: same candidates in deliberately different orders must select same release
# This verifies selection is based on intrinsic properties, not input order
$distinctReleases = @(
    [PSCustomObject]@{ title = "Album"; guid = "guid-c"; size = 100; indexerId = 1 }
    [PSCustomObject]@{ title = "Album"; guid = "guid-a"; size = 200; indexerId = 2 }
    [PSCustomObject]@{ title = "Album"; guid = "guid-b"; size = 150; indexerId = 3 }
)
# Reverse order
$reversedReleases = @(
    [PSCustomObject]@{ title = "Album"; guid = "guid-b"; size = 150; indexerId = 3 }
    [PSCustomObject]@{ title = "Album"; guid = "guid-a"; size = 200; indexerId = 2 }
    [PSCustomObject]@{ title = "Album"; guid = "guid-c"; size = 100; indexerId = 1 }
)
# Random middle order
$middleOrderReleases = @(
    [PSCustomObject]@{ title = "Album"; guid = "guid-a"; size = 200; indexerId = 2 }
    [PSCustomObject]@{ title = "Album"; guid = "guid-c"; size = 100; indexerId = 1 }
    [PSCustomObject]@{ title = "Album"; guid = "guid-b"; size = 150; indexerId = 3 }
)

$result1 = Select-DeterministicRelease -Releases $distinctReleases -ReturnSelectionBasis
$result2 = Select-DeterministicRelease -Releases $reversedReleases -ReturnSelectionBasis
$result3 = Select-DeterministicRelease -Releases $middleOrderReleases -ReturnSelectionBasis

# All three must select the same release (guid-a, which is alphabetically first)
Assert-True -Condition ($result1.release.guid -eq 'guid-a') -Description "Order 1 selects guid-a (got '$($result1.release.guid)')"
Assert-True -Condition ($result2.release.guid -eq 'guid-a') -Description "Order 2 (reversed) selects guid-a (got '$($result2.release.guid)')"
Assert-True -Condition ($result3.release.guid -eq 'guid-a') -Description "Order 3 (middle) selects guid-a (got '$($result3.release.guid)')"

# Verify intrinsicHash is consistent across all three (same release properties = same hash)
Assert-True -Condition ($result1.selectionBasis.selectedIntrinsicHash -eq $result2.selectionBasis.selectedIntrinsicHash) -Description "IntrinsicHash consistent between order 1 and 2"
Assert-True -Condition ($result2.selectionBasis.selectedIntrinsicHash -eq $result3.selectionBasis.selectedIntrinsicHash) -Description "IntrinsicHash consistent between order 2 and 3"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary: $passed passed, $failed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
