#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Hermetic unit tests for Get-E2ESourcesContext (no docker required).
.DESCRIPTION
    Validates:
    - Git SHA extraction from real git repos
    - Case-insensitive repo path probing (Linux CI compatibility)
    - Override behavior via RepoPathOverrides
    - Graceful null handling when repos don't exist
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$libDir = Join-Path $PSScriptRoot '../lib'
Import-Module (Join-Path $libDir 'e2e-sources.psm1') -Force

$testRoot = $null
$failed = 0

function Initialize-TestRepoStructure {
    <#
    .SYNOPSIS
        Creates a fake repo layout with initialized git repos.
    .DESCRIPTION
        Layout:
        $testRoot/
          Common/           (git repo)
          Qobuzarr/         (git repo, exact casing)
          tidalarr/         (git repo, lowercase - tests case probing)
          BrainarrOverride/ (git repo, used for override test)
    #>
    param([string]$Root)

    # Create directories
    $commonDir = Join-Path $Root 'Common'
    $qobuzarrDir = Join-Path $Root 'Qobuzarr'
    $tidalarrDir = Join-Path $Root 'tidalarr'  # lowercase intentionally
    $brainarrOverrideDir = Join-Path $Root 'BrainarrOverride'

    New-Item -ItemType Directory -Path $commonDir -Force | Out-Null
    New-Item -ItemType Directory -Path $qobuzarrDir -Force | Out-Null
    New-Item -ItemType Directory -Path $tidalarrDir -Force | Out-Null
    New-Item -ItemType Directory -Path $brainarrOverrideDir -Force | Out-Null

    # Initialize git repos with a commit
    foreach ($dir in @($commonDir, $qobuzarrDir, $tidalarrDir, $brainarrOverrideDir)) {
        Push-Location $dir
        try {
            git init --quiet 2>$null
            git config user.email "test@test.com" 2>$null
            git config user.name "Test" 2>$null
            # Create a file and commit so HEAD exists
            "test" | Out-File -FilePath (Join-Path $dir 'README.md') -Encoding UTF8
            git add README.md 2>$null
            git commit -m "Initial commit" --quiet 2>$null
        }
        finally {
            Pop-Location
        }
    }

    return @{
        Common = $commonDir
        Qobuzarr = $qobuzarrDir
        Tidalarr = $tidalarrDir
        BrainarrOverride = $brainarrOverrideDir
    }
}

function Test-BasicShaExtraction {
    param([hashtable]$Repos)

    Write-Host "`n[TEST] Basic SHA extraction..." -ForegroundColor Cyan

    $result = Get-E2ESourcesContext `
        -CommonRepoRoot $Repos.Common `
        -Plugins @('Qobuzarr') `
        -ContainerName $null

    # Common SHA should be extracted
    if (-not $result.SourceShas['Common']) {
        Write-Host "  [FAIL] Common SHA is null" -ForegroundColor Red
        return $false
    }
    if ($result.SourceShas['Common'].Length -ne 7) {
        Write-Host "  [FAIL] Common SHA not 7 chars: $($result.SourceShas['Common'])" -ForegroundColor Red
        return $false
    }
    Write-Host "  [PASS] Common SHA: $($result.SourceShas['Common'])" -ForegroundColor Green

    # Common fullSha should be extracted (40 chars)
    if (-not $result.SourceFullShas['Common']) {
        Write-Host "  [FAIL] Common fullSha is null" -ForegroundColor Red
        return $false
    }
    if ($result.SourceFullShas['Common'].Length -ne 40) {
        Write-Host "  [FAIL] Common fullSha not 40 chars: $($result.SourceFullShas['Common'])" -ForegroundColor Red
        return $false
    }
    Write-Host "  [PASS] Common fullSha: $($result.SourceFullShas['Common'])" -ForegroundColor Green

    # Qobuzarr SHA should be extracted (exact casing match)
    if (-not $result.SourceShas['Qobuzarr']) {
        Write-Host "  [FAIL] Qobuzarr SHA is null" -ForegroundColor Red
        return $false
    }
    Write-Host "  [PASS] Qobuzarr SHA: $($result.SourceShas['Qobuzarr'])" -ForegroundColor Green

    # Qobuzarr fullSha should be extracted
    if (-not $result.SourceFullShas['Qobuzarr']) {
        Write-Host "  [FAIL] Qobuzarr fullSha is null" -ForegroundColor Red
        return $false
    }
    Write-Host "  [PASS] Qobuzarr fullSha: $($result.SourceFullShas['Qobuzarr'])" -ForegroundColor Green

    # Provenance should be 'git'
    if ($result.SourceProvenance['Common'] -ne 'git') {
        Write-Host "  [FAIL] Common provenance not 'git': $($result.SourceProvenance['Common'])" -ForegroundColor Red
        return $false
    }
    if ($result.SourceProvenance['Qobuzarr'] -ne 'git') {
        Write-Host "  [FAIL] Qobuzarr provenance not 'git': $($result.SourceProvenance['Qobuzarr'])" -ForegroundColor Red
        return $false
    }
    Write-Host "  [PASS] Provenance values correct" -ForegroundColor Green

    return $true
}

function Test-CaseInsensitiveProbing {
    param([hashtable]$Repos)

    Write-Host "`n[TEST] Case-insensitive repo probing (Linux CI)..." -ForegroundColor Cyan

    # tidalarr is lowercase in filesystem, but we request 'Tidalarr'
    # Find-RepoPath should probe lowercase variant and find it
    $result = Get-E2ESourcesContext `
        -CommonRepoRoot $Repos.Common `
        -Plugins @('Tidalarr') `
        -ContainerName $null

    # Should find tidalarr/ even when requesting Tidalarr
    if (-not $result.SourceShas['Tidalarr']) {
        Write-Host "  [FAIL] Tidalarr SHA is null - case probing failed" -ForegroundColor Red
        Write-Host "         This will cause Linux CI to regress to sha: null" -ForegroundColor Yellow
        return $false
    }
    Write-Host "  [PASS] Tidalarr SHA found via lowercase probe: $($result.SourceShas['Tidalarr'])" -ForegroundColor Green

    if ($result.SourceProvenance['Tidalarr'] -ne 'git') {
        Write-Host "  [FAIL] Tidalarr provenance not 'git'" -ForegroundColor Red
        return $false
    }
    Write-Host "  [PASS] Provenance correct for case-probed repo" -ForegroundColor Green

    return $true
}

function Test-OverrideBehavior {
    param([hashtable]$Repos)

    Write-Host "`n[TEST] RepoPathOverrides behavior..." -ForegroundColor Cyan

    # Brainarr doesn't exist in normal layout, but we override to BrainarrOverride
    $overrides = @{
        'Brainarr' = $Repos.BrainarrOverride
    }

    $result = Get-E2ESourcesContext `
        -CommonRepoRoot $Repos.Common `
        -Plugins @('Brainarr') `
        -ContainerName $null `
        -RepoPathOverrides $overrides

    if (-not $result.SourceShas['Brainarr']) {
        Write-Host "  [FAIL] Brainarr SHA is null - override not honored" -ForegroundColor Red
        return $false
    }
    Write-Host "  [PASS] Brainarr SHA from override: $($result.SourceShas['Brainarr'])" -ForegroundColor Green

    return $true
}

function Test-MissingRepoGraceful {
    param([hashtable]$Repos)

    Write-Host "`n[TEST] Missing repo returns null gracefully..." -ForegroundColor Cyan

    # NonExistent plugin should return null SHA and 'unknown' provenance
    $result = Get-E2ESourcesContext `
        -CommonRepoRoot $Repos.Common `
        -Plugins @('NonExistentPlugin') `
        -ContainerName $null

    if ($null -ne $result.SourceShas['NonExistentPlugin']) {
        Write-Host "  [FAIL] NonExistentPlugin SHA should be null" -ForegroundColor Red
        return $false
    }
    Write-Host "  [PASS] NonExistentPlugin SHA is null" -ForegroundColor Green

    if ($result.SourceProvenance['NonExistentPlugin'] -ne 'unknown') {
        Write-Host "  [FAIL] NonExistentPlugin provenance should be 'unknown': $($result.SourceProvenance['NonExistentPlugin'])" -ForegroundColor Red
        return $false
    }
    Write-Host "  [PASS] NonExistentPlugin provenance is 'unknown'" -ForegroundColor Green

    return $true
}

function Test-MultiplePlugins {
    param([hashtable]$Repos)

    Write-Host "`n[TEST] Multiple plugins in single call..." -ForegroundColor Cyan

    $result = Get-E2ESourcesContext `
        -CommonRepoRoot $Repos.Common `
        -Plugins @('Qobuzarr', 'Tidalarr') `
        -ContainerName $null

    if (-not $result.SourceShas['Qobuzarr']) {
        Write-Host "  [FAIL] Qobuzarr SHA missing in multi-plugin call" -ForegroundColor Red
        return $false
    }
    if (-not $result.SourceShas['Tidalarr']) {
        Write-Host "  [FAIL] Tidalarr SHA missing in multi-plugin call" -ForegroundColor Red
        return $false
    }
    Write-Host "  [PASS] Both plugins resolved in single call" -ForegroundColor Green

    return $true
}

# --- Main ---

try {
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Test-SourcesContext: Hermetic Unit Tests" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    # Create temp test structure
    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "e2e-sources-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    Write-Host "`nCreating test repo structure at: $testRoot" -ForegroundColor Gray

    $repos = Initialize-TestRepoStructure -Root $testRoot

    # Run tests
    if (-not (Test-BasicShaExtraction -Repos $repos)) { $failed++ }
    if (-not (Test-CaseInsensitiveProbing -Repos $repos)) { $failed++ }
    if (-not (Test-OverrideBehavior -Repos $repos)) { $failed++ }
    if (-not (Test-MissingRepoGraceful -Repos $repos)) { $failed++ }
    if (-not (Test-MultiplePlugins -Repos $repos)) { $failed++ }

    Write-Host "`n========================================" -ForegroundColor Cyan
    if ($failed -gt 0) {
        Write-Host "FAILED: $failed test(s) failed" -ForegroundColor Red
        exit 1
    }

    Write-Host "PASS: Test-SourcesContext (all tests passed)" -ForegroundColor Green
    exit 0
}
finally {
    # Cleanup
    if ($testRoot -and (Test-Path $testRoot)) {
        Write-Host "`nCleaning up test directory..." -ForegroundColor Gray
        Remove-Item -Recurse -Force $testRoot -ErrorAction SilentlyContinue
    }
}
