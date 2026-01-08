#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validates that all E2E error codes are documented.

.DESCRIPTION
    Ensures docs/E2E_ERROR_CODES.md stays in sync with actual error codes
    used in the codebase. Prevents "added code but forgot to document" drift.

    Strict sources (blocking):
    - Golden manifests: scripts/tests/fixtures/golden-manifests/*.json
    - Other fixtures: scripts/tests/fixtures/**/*.json
    - PowerShell ErrorCode assignments: ErrorCode = "E2E_..."

    Gate-local sources (warning only):
    - Error array additions: $result.Errors += "E2E_..."
    - These are contextual strings, not machine-parsed manifest codes.

.NOTES
    Run as: pwsh scripts/tests/Test-ErrorCodesDocsSync.ps1
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "E2E Error Codes Documentation Sync Test" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$scriptRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $scriptRoot -Parent
$docsPath = Join-Path $repoRoot "docs/E2E_ERROR_CODES.md"
$fixturesPath = Join-Path $scriptRoot "tests/fixtures"
$goldenManifestsPath = Join-Path $fixturesPath "golden-manifests"

$passed = 0
$failed = 0
$warnings = 0

function Assert-True {
    param(
        [Parameter(Mandatory)] [bool]$Condition,
        [Parameter(Mandatory)] [string]$Description
    )
    if ($Condition) {
        Write-Host "  [PASS] $Description" -ForegroundColor Green
        $script:passed++
        return $true
    } else {
        Write-Host "  [FAIL] $Description" -ForegroundColor Red
        $script:failed++
        return $false
    }
}

function Write-Warning-Result {
    param([string]$Message)
    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
    $script:warnings++
}

# ============================================================
# Step 1: Read documented codes from E2E_ERROR_CODES.md
# ============================================================
Write-Host "`nStep 1: Reading documented codes from E2E_ERROR_CODES.md" -ForegroundColor Yellow

if (-not (Test-Path $docsPath)) {
    Write-Host "  [FAIL] docs/E2E_ERROR_CODES.md not found at: $docsPath" -ForegroundColor Red
    exit 1
}

$docsContent = Get-Content $docsPath -Raw
$documentedCodes = @()

# Extract codes from table rows: | `E2E_CODE_NAME` |
$tableMatches = [regex]::Matches($docsContent, '\|\s*`(E2E_[A-Z0-9_]+)`\s*\|')
foreach ($match in $tableMatches) {
    $documentedCodes += $match.Groups[1].Value
}

$documentedCodes = $documentedCodes | Sort-Object -Unique
Write-Host "  Found $($documentedCodes.Count) documented codes" -ForegroundColor Cyan

# ============================================================
# Step 2: Collect manifest errorCode values from fixtures
# ============================================================
Write-Host "`nStep 2: Collecting manifest errorCode values from fixtures" -ForegroundColor Yellow

$manifestCodes = @()
$manifestSources = @{}  # Track where each code was found

# Golden manifests
if (Test-Path $goldenManifestsPath) {
    $goldenFiles = Get-ChildItem -Path $goldenManifestsPath -Filter "*.json" -ErrorAction SilentlyContinue
    foreach ($file in $goldenFiles) {
        try {
            $json = Get-Content $file.FullName -Raw | ConvertFrom-Json -Depth 10

            # Check results[].errorCode
            if ($json.results) {
                foreach ($result in $json.results) {
                    if ($result.errorCode -and $result.errorCode -match '^E2E_') {
                        $code = $result.errorCode
                        $manifestCodes += $code
                        if (-not $manifestSources[$code]) { $manifestSources[$code] = @() }
                        $manifestSources[$code] += "golden-manifests/$($file.Name)"
                    }
                }
            }
        } catch {
            Write-Host "  Warning: Could not parse $($file.Name): $_" -ForegroundColor Yellow
        }
    }
}

# Other fixture directories
$otherFixtureDirs = @("cold-start")
foreach ($subdir in $otherFixtureDirs) {
    $subdirPath = Join-Path $fixturesPath $subdir
    if (Test-Path $subdirPath) {
        $fixtureFiles = Get-ChildItem -Path $subdirPath -Filter "*.json" -ErrorAction SilentlyContinue
        foreach ($file in $fixtureFiles) {
            try {
                $json = Get-Content $file.FullName -Raw | ConvertFrom-Json -Depth 10

                if ($json.results) {
                    foreach ($result in $json.results) {
                        if ($result.errorCode -and $result.errorCode -match '^E2E_') {
                            $code = $result.errorCode
                            $manifestCodes += $code
                            if (-not $manifestSources[$code]) { $manifestSources[$code] = @() }
                            $manifestSources[$code] += "$subdir/$($file.Name)"
                        }
                    }
                }
            } catch {
                # Ignore parse errors for non-manifest fixtures
            }
        }
    }
}

$manifestCodes = $manifestCodes | Sort-Object -Unique
Write-Host "  Found $($manifestCodes.Count) unique codes in fixtures" -ForegroundColor Cyan

# ============================================================
# Step 3: Collect ErrorCode assignments from PowerShell sources
# ============================================================
Write-Host "`nStep 3: Collecting ErrorCode assignments from PowerShell sources" -ForegroundColor Yellow

$assignmentCodesList = @()
$assignmentSources = @{}

# Search patterns for ErrorCode assignments and definitions
# - ErrorCode = "E2E_..."
# - .ErrorCode = "E2E_..."
# - 'ErrorCode' = "E2E_..."
# - 'E2E_...' = @(...) in error classifier hashtable
$psFiles = Get-ChildItem -Path $scriptRoot -Recurse -Include "*.ps1", "*.psm1" -ErrorAction SilentlyContinue

foreach ($file in $psFiles) {
    $content = Get-Content $file.FullName -Raw
    $relativePath = $file.FullName.Replace($repoRoot, '').TrimStart('\', '/')

    # Match: ErrorCode = "E2E_..." or 'ErrorCode' = "E2E_..."
    $regexMatches = [regex]::Matches($content, '(?:\.?ErrorCode\s*=\s*[''"]|[''"]ErrorCode[''"]\s*=\s*[''"])(E2E_[A-Z0-9_]+)[''"]')
    foreach ($match in $regexMatches) {
        $code = $match.Groups[1].Value
        $assignmentCodesList += $code
        if (-not $assignmentSources[$code]) { $assignmentSources[$code] = @() }

        # Find line number
        $beforeMatch = $content.Substring(0, $match.Index)
        $lineNumber = ($beforeMatch -split "`n").Count
        $assignmentSources[$code] += "${relativePath}:${lineNumber}"
    }

    # Match: 'E2E_...' = @(...) - error classifier hashtable definitions
    $classifierPattern = '[''"](?<code>E2E_[A-Z0-9_]+)[''"][ \t]*=[ \t]*@\('
    $classifierMatches = [regex]::Matches($content, $classifierPattern)
    foreach ($match in $classifierMatches) {
        $code = $match.Groups['code'].Value
        $assignmentCodesList += $code
        if (-not $assignmentSources[$code]) { $assignmentSources[$code] = @() }

        $beforeMatch = $content.Substring(0, $match.Index)
        $lineNumber = ($beforeMatch -split "`n").Count
        $assignmentSources[$code] += "${relativePath}:${lineNumber} (classifier definition)"
    }
}

$assignmentCodes = @($assignmentCodesList | Sort-Object -Unique)
Write-Host "  Found $($assignmentCodes.Count) unique codes in ErrorCode assignments" -ForegroundColor Cyan

# ============================================================
# Step 4: Collect gate-local error strings (warning only)
# ============================================================
Write-Host "`nStep 4: Collecting gate-local error strings (warning only)" -ForegroundColor Yellow

$gateLocalCodesList = @()
$gateLocalSources = @{}

foreach ($file in $psFiles) {
    $content = Get-Content $file.FullName -Raw
    $relativePath = $file.FullName.Replace($repoRoot, '').TrimStart('\', '/')

    # Match: .Errors += "E2E_..." or throw "E2E_..."
    $regexMatches = [regex]::Matches($content, '(?:\.Errors\s*\+=\s*[''"]|throw\s*[''"])(E2E_[A-Z0-9_]+)[:\s]')
    foreach ($match in $regexMatches) {
        $code = $match.Groups[1].Value
        $gateLocalCodesList += $code
        if (-not $gateLocalSources[$code]) { $gateLocalSources[$code] = @() }

        $beforeMatch = $content.Substring(0, $match.Index)
        $lineNumber = ($beforeMatch -split "`n").Count
        $gateLocalSources[$code] += "${relativePath}:${lineNumber}"
    }
}

$gateLocalCodes = @($gateLocalCodesList | Sort-Object -Unique)
Write-Host "  Found $($gateLocalCodes.Count) unique gate-local codes" -ForegroundColor Cyan

# ============================================================
# Step 5: Validate strict sources are documented
# ============================================================
Write-Host "`nStep 5: Validating strict sources are documented" -ForegroundColor Yellow

$allStrictCodes = @(($manifestCodes + $assignmentCodes) | Sort-Object -Unique)
$missingCodes = @()

foreach ($code in $allStrictCodes) {
    if ($code -notin $documentedCodes) {
        $missingCodes += $code
    }
}

if ($missingCodes.Count -eq 0) {
    Assert-True -Condition $true -Description "All $($allStrictCodes.Count) strict error codes are documented"
} else {
    Assert-True -Condition $false -Description "All strict error codes are documented"

    Write-Host "`n  Missing codes in docs/E2E_ERROR_CODES.md:" -ForegroundColor Red
    foreach ($code in $missingCodes) {
        Write-Host "    - $code" -ForegroundColor Red

        # Show sources
        if ($manifestSources[$code]) {
            foreach ($src in $manifestSources[$code]) {
                Write-Host "        Found in fixture: $src" -ForegroundColor DarkGray
            }
        }
        if ($assignmentSources[$code]) {
            foreach ($src in $assignmentSources[$code]) {
                Write-Host "        Found in source: $src" -ForegroundColor DarkGray
            }
        }
    }
}

# ============================================================
# Step 6: Warn about undocumented gate-local codes
# ============================================================
Write-Host "`nStep 6: Checking gate-local codes (non-blocking)" -ForegroundColor Yellow

$undocumentedGateLocal = @()
foreach ($code in $gateLocalCodes) {
    if ($code -notin $documentedCodes -and $code -notin $allStrictCodes) {
        $undocumentedGateLocal += $code
    }
}

if ($undocumentedGateLocal.Count -eq 0) {
    Write-Host "  [INFO] All gate-local codes are documented or covered by strict sources" -ForegroundColor Cyan
} else {
    Write-Warning-Result "Undocumented gate-local codes (consider documenting):"
    foreach ($code in $undocumentedGateLocal) {
        Write-Host "    - $code" -ForegroundColor Yellow
        if ($gateLocalSources[$code]) {
            foreach ($src in $gateLocalSources[$code]) {
                Write-Host "        Found in: $src" -ForegroundColor DarkGray
            }
        }
    }
}

# ============================================================
# Step 7: Check for orphaned documented codes
# ============================================================
Write-Host "`nStep 7: Checking for orphaned documented codes" -ForegroundColor Yellow

$allCodeSources = @(($allStrictCodes + $gateLocalCodes) | Sort-Object -Unique)
$orphanedCodes = @()

foreach ($code in $documentedCodes) {
    if ($code -notin $allCodeSources) {
        $orphanedCodes += $code
    }
}

if ($orphanedCodes.Count -eq 0) {
    Write-Host "  [INFO] No orphaned codes in documentation" -ForegroundColor Cyan
} else {
    Write-Warning-Result "Documented codes not found in codebase (may be stale):"
    foreach ($code in $orphanedCodes) {
        Write-Host "    - $code" -ForegroundColor Yellow
    }
}

# ============================================================
# Summary
# ============================================================
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary: $passed passed, $failed failed, $warnings warnings" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) {
    Write-Host "`nAction required: Add missing codes to docs/E2E_ERROR_CODES.md" -ForegroundColor Red
    exit 1
}

if ($warnings -gt 0) {
    Write-Host "`nConsider addressing warnings for complete coverage." -ForegroundColor Yellow
}

exit 0
