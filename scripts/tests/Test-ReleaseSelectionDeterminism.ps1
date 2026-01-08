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

# Simulate the release selection logic from e2e-runner.ps1
function Select-DeterministicRelease {
    param([array]$Releases)

    return $Releases | Sort-Object -Stable `
        @{Expression = { ($_.title ?? '').ToUpperInvariant() }; Ascending = $true },
        @{Expression = { ($_.guid ?? '').ToUpperInvariant() }; Ascending = $true },
        @{Expression = { $_.size ?? 0 }; Descending = $true } |
        Select-Object -First 1
}

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

Write-Host "`nTest 6: Size is tertiary tie-breaker (descending)" -ForegroundColor Yellow
$sameTitleGuid = @(
    [PSCustomObject]@{ title = "Same"; guid = "same-guid"; size = 100 }
    [PSCustomObject]@{ title = "Same"; guid = "same-guid"; size = 300 }
    [PSCustomObject]@{ title = "Same"; guid = "same-guid"; size = 200 }
)
$selectedBySize = Select-DeterministicRelease -Releases $sameTitleGuid
Assert-True -Condition ($selectedBySize.size -eq 300) -Description "Largest size wins as tertiary tie-breaker (got $($selectedBySize.size))"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary: $passed passed, $failed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
