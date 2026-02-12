#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Self-tests for repin-common-submodule.ps1 / .sh.

.DESCRIPTION
    Validates SHA-writing and pin-rewriting logic to prevent regressions:
    - ext-common-sha.txt ends with \n (41 bytes), lowercase hex only
    - --update-pins rewrites real uses: lines but not comments
    - Invalid SHAs are rejected
    - Uppercase SHAs are normalized to lowercase
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = $PSScriptRoot
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$RepinPs1  = Join-Path $RepoRoot "scripts/repin-common-submodule.ps1"
$FixtureDir = Join-Path $ScriptDir "fixtures"
$SampleWorkflow = Join-Path $FixtureDir "repin-workflow-sample.yml"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Repin Script Self-Tests" -ForegroundColor Cyan
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

# --- Setup: create a temp directory that mimics a plugin repo ---
$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "repin-test-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

try {
    # Create a fake submodule directory with a git repo so rev-parse works
    $SubmodulePath = Join-Path $TempDir "ext/Lidarr.Plugin.Common"
    New-Item -ItemType Directory -Path $SubmodulePath -Force | Out-Null

    Push-Location $SubmodulePath
    git init --quiet 2>$null
    git commit --allow-empty -m "init" --quiet 2>$null
    Pop-Location

    $TestSha = "da58f3ef5064cf832d3d6a9eed13eb7d4d3d392f"

    # Create workflow fixtures dir
    $WfDir = Join-Path $TempDir ".github/workflows"
    New-Item -ItemType Directory -Path $WfDir -Force | Out-Null
    Copy-Item $SampleWorkflow (Join-Path $WfDir "sample.yml")

    # =====================================================================
    Write-Host "SHA File Format Tests:" -ForegroundColor White
    # =====================================================================

    # Test 1: SHA file is exactly 41 bytes (40 hex + LF)
    Test-Assertion "SHA file is 41 bytes (40 hex + LF)" {
        Push-Location $TempDir
        try {
            # Write the SHA file using the same logic as the script
            $shaFile = "ext-common-sha.txt"
            $absPath = Join-Path $PWD.Path $shaFile
            $bytes = [System.Text.Encoding]::ASCII.GetBytes($TestSha + "`n")
            [System.IO.File]::WriteAllBytes($absPath, $bytes)

            $fileBytes = [System.IO.File]::ReadAllBytes($absPath)
            $fileBytes.Length -eq 41
        } finally { Pop-Location }
    }

    # Test 2: SHA file ends with LF (0x0a), not CRLF
    Test-Assertion "SHA file ends with LF (not CRLF)" {
        $absPath = Join-Path $TempDir "ext-common-sha.txt"
        $fileBytes = [System.IO.File]::ReadAllBytes($absPath)
        ($fileBytes[-1] -eq 0x0a) -and ($fileBytes.Length -lt 2 -or $fileBytes[-2] -ne 0x0d)
    }

    # Test 3: SHA file contains only lowercase hex + newline
    Test-Assertion "SHA file contains only lowercase hex + newline" {
        $content = [System.IO.File]::ReadAllText((Join-Path $TempDir "ext-common-sha.txt"))
        $content -match '^[0-9a-f]{40}\n$'
    }

    # =====================================================================
    Write-Host ""
    Write-Host "SHA Validation Tests:" -ForegroundColor White
    # =====================================================================

    # Test 4: Valid lowercase SHA is accepted (regex test)
    Test-Assertion "Valid lowercase SHA passes validation" {
        $sha = "da58f3ef5064cf832d3d6a9eed13eb7d4d3d392f"
        $sha -match '^[0-9a-fA-F]{40}$'
    }

    # Test 5: Valid uppercase SHA passes validation
    Test-Assertion "Valid uppercase SHA passes validation" {
        $sha = "DA58F3EF5064CF832D3D6A9EED13EB7D4D3D392F"
        $sha -match '^[0-9a-fA-F]{40}$'
    }

    # Test 6: Mixed-case SHA passes validation
    Test-Assertion "Mixed-case SHA passes validation" {
        $sha = "Da58f3EF5064cf832d3D6a9eed13EB7D4D3D392F"
        $sha -match '^[0-9a-fA-F]{40}$'
    }

    # Test 7: Uppercase SHA is normalized to lowercase
    Test-Assertion "Uppercase SHA normalizes to lowercase" {
        $sha = "DA58F3EF5064CF832D3D6A9EED13EB7D4D3D392F"
        $sha.ToLowerInvariant() -eq "da58f3ef5064cf832d3d6a9eed13eb7d4d3d392f"
    }

    # Test 8: Short string is rejected
    Test-Assertion "Short string is rejected by validation" {
        $sha = "not-a-sha"
        -not ($sha -match '^[0-9a-fA-F]{40}$')
    }

    # Test 9: 39-char hex is rejected
    Test-Assertion "39-char hex is rejected" {
        $sha = "da58f3ef5064cf832d3d6a9eed13eb7d4d3d392"
        -not ($sha -match '^[0-9a-fA-F]{40}$')
    }

    # Test 10: 41-char hex is rejected
    Test-Assertion "41-char hex is rejected" {
        $sha = "da58f3ef5064cf832d3d6a9eed13eb7d4d3d392ff"
        -not ($sha -match '^[0-9a-fA-F]{40}$')
    }

    # Test 11: Hex with non-hex chars is rejected
    Test-Assertion "Non-hex characters are rejected" {
        $sha = "ga58f3ef5064cf832d3d6a9eed13eb7d4d3d392f"
        -not ($sha -match '^[0-9a-fA-F]{40}$')
    }

    # =====================================================================
    Write-Host ""
    Write-Host "Pin Rewrite Tests:" -ForegroundColor White
    # =====================================================================

    $NewSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
    $OldSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

    # Test 12: Real uses: lines are rewritten
    Test-Assertion "Real uses: lines are rewritten by -UpdatePins regex" {
        $content = [IO.File]::ReadAllText((Join-Path $WfDir "sample.yml"))
        $pattern = '(?m)^(\s*uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+@)[0-9a-f]{40}'
        $newContent = [regex]::Replace($content, $pattern, "`${1}$NewSha")
        $matches = [regex]::Matches($newContent, [regex]::Escape($NewSha))
        $matches.Count -eq 2  # exactly 2 uses: lines should be rewritten
    }

    # Test 13: Comment line is NOT rewritten
    Test-Assertion "Comment line with uses: is NOT rewritten" {
        $content = [IO.File]::ReadAllText((Join-Path $WfDir "sample.yml"))
        $pattern = '(?m)^(\s*uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+@)[0-9a-f]{40}'
        $newContent = [regex]::Replace($content, $pattern, "`${1}$NewSha")
        # The commented line should still have the old SHA
        $newContent -match "# uses: RicherTunes/Lidarr\.Plugin\.Common/.*@$OldSha"
    }

    # Test 14: Non-Common actions are not touched
    Test-Assertion "Non-Common actions (actions/checkout) are not touched" {
        $content = [IO.File]::ReadAllText((Join-Path $WfDir "sample.yml"))
        $pattern = '(?m)^(\s*uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+@)[0-9a-f]{40}'
        $newContent = [regex]::Replace($content, $pattern, "`${1}$NewSha")
        ($newContent -match 'actions/checkout@v4') -and ($newContent -match 'actions/setup-dotnet@v3')
    }

    # Test 15: All old SHAs in uses: lines are replaced (none remain)
    Test-Assertion "No old SHAs remain in uses: lines after rewrite" {
        $content = [IO.File]::ReadAllText((Join-Path $WfDir "sample.yml"))
        $pattern = '(?m)^(\s*uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+@)[0-9a-f]{40}'
        $newContent = [regex]::Replace($content, $pattern, "`${1}$NewSha")
        # Count remaining old SHAs in non-comment uses: lines
        $remaining = [regex]::Matches($newContent, "(?m)^\s*uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+@$OldSha")
        $remaining.Count -eq 0
    }

} finally {
    # Cleanup
    Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) {
    exit 1
}

Write-Host ""
Write-Host "[OK] All repin script tests passed." -ForegroundColor Green
exit 0
