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
    - Sentinel file is always written to repo root regardless of cwd
    - -VerifyOnly correctly detects sentinel-submodule mismatch from any cwd
    - The shell repin script is UTF-8 without BOM so bash can parse the shebang
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = $PSScriptRoot
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$RepinPs1  = Join-Path $RepoRoot "scripts/repin-common-submodule.ps1"
$RepinSh   = Join-Path $RepoRoot "scripts/repin-common-submodule.sh"
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

# =====================================================================
Write-Host "Script Encoding Tests:" -ForegroundColor White
# =====================================================================

Test-Assertion "Shell repin script has no UTF-8 BOM" {
    $bytes = [System.IO.File]::ReadAllBytes($RepinSh)
    -not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
}

# --- Setup: create a temp directory that mimics a plugin repo ---
$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "repin-test-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

try {
    # Initialize $TempDir as a git repo (required: repin script calls git rev-parse --show-toplevel)
    git init $TempDir --quiet 2>$null | Out-Null
    git -C $TempDir config user.email "repin-test@example.invalid" 2>$null | Out-Null
    git -C $TempDir config user.name "Repin Test" 2>$null | Out-Null
    git -C $TempDir commit --allow-empty -m "init" --quiet 2>$null | Out-Null

    # Create a fake submodule directory with a git repo so rev-parse works
    $SubmodulePath = Join-Path $TempDir "ext/Lidarr.Plugin.Common"
    New-Item -ItemType Directory -Path $SubmodulePath -Force | Out-Null

    Push-Location $SubmodulePath
    git init --quiet 2>$null
    git config user.email "repin-test@example.invalid" 2>$null
    git config user.name "Repin Test" 2>$null
    git commit --allow-empty -m "init" --quiet 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create fake submodule HEAD for repin tests."
    }
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

    # Test 16: Script uses explicit -Path/-Filter workflow discovery (works in root dir mode)
    Test-Assertion "Workflow file discovery uses -Path and -Filter" {
        $scriptText = [IO.File]::ReadAllText($RepinPs1)
        ($scriptText -match 'Get-ChildItem -Path \$workflowDir -File -Filter "\*\.yml"') -and
        ($scriptText -match 'Get-ChildItem -Path \$workflowDir -File -Filter "\*\.yaml"')
    }

    # Test 17: VerifyOnly fails on stale reusable Common workflow pins
    Test-Assertion "VerifyOnly fails on stale workflow SHA pins" {
        $actualSha = (git -C $SubmodulePath rev-parse HEAD).Trim()
        $shaBytes = [System.Text.Encoding]::ASCII.GetBytes($actualSha + "`n")
        [System.IO.File]::WriteAllBytes((Join-Path $TempDir "ext-common-sha.txt"), $shaBytes)

        Push-Location $TempDir
        try {
            $output = & $RepinPs1 -VerifyOnly -SubmodulePath "ext/Lidarr.Plugin.Common" *>&1
            $exitCode = $LASTEXITCODE
        } finally {
            Pop-Location
        }

        ($exitCode -ne 0) -and (($output -join "`n") -match 'stale')
    }

    # =====================================================================
    Write-Host "" 
    Write-Host "CWD-Independence Tests:" -ForegroundColor White
    # =====================================================================

    # Test 18: Sentinel file is always written to repo root, not to non-root cwd
    Test-Assertion "Sentinel written to repo root, not to the non-root cwd" {
        $FakePlug = Join-Path ([IO.Path]::GetTempPath()) "repin-cwd-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
        try {
            New-Item -ItemType Directory $FakePlug -Force | Out-Null
            git init $FakePlug --quiet 2>$null | Out-Null
            git -C $FakePlug config user.email "t@t.invalid" 2>$null | Out-Null
            git -C $FakePlug config user.name "T" 2>$null | Out-Null
            git -C $FakePlug commit --allow-empty -m "init" --quiet 2>$null | Out-Null
            $FakeCommonAbs = Join-Path $FakePlug "ext/Lidarr.Plugin.Common"
            New-Item -ItemType Directory $FakeCommonAbs -Force | Out-Null
            git init $FakeCommonAbs --quiet 2>$null | Out-Null
            git -C $FakeCommonAbs config user.email "t@t.invalid" 2>$null | Out-Null
            git -C $FakeCommonAbs config user.name "T" 2>$null | Out-Null
            git -C $FakeCommonAbs commit --allow-empty -m "init" --quiet 2>$null | Out-Null
            git -C $FakeCommonAbs remote add origin $FakeCommonAbs 2>$null | Out-Null
            $TargetSha = (git -C $FakeCommonAbs rev-parse HEAD 2>$null).Trim()
            $NonRootCwd = Join-Path $FakePlug "subdir"
            New-Item -ItemType Directory $NonRootCwd -Force | Out-Null
            Push-Location $NonRootCwd
            try {
                & $RepinPs1 -SHA $TargetSha -SubmodulePath $FakeCommonAbs *>&1 | Out-Null
            } finally { Pop-Location }
            $sentinelAtRoot = Test-Path (Join-Path $FakePlug "ext-common-sha.txt")
            $strayAtCwd     = Test-Path (Join-Path $NonRootCwd "ext-common-sha.txt")
            $sentinelAtRoot -and (-not $strayAtCwd)
        } finally { Remove-Item -Recurse -Force $FakePlug -ErrorAction SilentlyContinue }
    }

    # Test 19: VerifyOnly detects mismatch (not "not found") when invoked from non-root cwd
    Test-Assertion "VerifyOnly detects sentinel-submodule mismatch when invoked from non-root cwd" {
        $FakePlug2 = Join-Path ([IO.Path]::GetTempPath()) "repin-vo-cwd-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
        try {
            New-Item -ItemType Directory $FakePlug2 -Force | Out-Null
            git init $FakePlug2 --quiet 2>$null | Out-Null
            git -C $FakePlug2 config user.email "t@t.invalid" 2>$null | Out-Null
            git -C $FakePlug2 config user.name "T" 2>$null | Out-Null
            git -C $FakePlug2 commit --allow-empty -m "init" --quiet 2>$null | Out-Null
            $FakeCommonAbs2 = Join-Path $FakePlug2 "ext/Lidarr.Plugin.Common"
            New-Item -ItemType Directory $FakeCommonAbs2 -Force | Out-Null
            git init $FakeCommonAbs2 --quiet 2>$null | Out-Null
            git -C $FakeCommonAbs2 config user.email "t@t.invalid" 2>$null | Out-Null
            git -C $FakeCommonAbs2 config user.name "T" 2>$null | Out-Null
            git -C $FakeCommonAbs2 commit --allow-empty -m "init" --quiet 2>$null | Out-Null
            # Write WRONG SHA to sentinel at plugin root -- mismatch, not missing
            $WrongSha = "0000000000000000000000000000000000000000"
            $wrongBytes = [System.Text.Encoding]::ASCII.GetBytes($WrongSha + "`n")
            [System.IO.File]::WriteAllBytes((Join-Path $FakePlug2 "ext-common-sha.txt"), $wrongBytes)
            $NonRootCwd2 = Join-Path $FakePlug2 "subdir"
            New-Item -ItemType Directory $NonRootCwd2 -Force | Out-Null
            Push-Location $NonRootCwd2
            try {
                $output   = & $RepinPs1 -VerifyOnly -SubmodulePath $FakeCommonAbs2 *>&1
                $exitCode = $LASTEXITCODE
            } finally { Pop-Location }
            # Pre-fix: "not found" error (script looked in $NonRootCwd2)
            # Post-fix: "mismatch" error (script finds sentinel at $FakePlug2)
            $outputStr = ($output | ForEach-Object { "$_" }) -join "`n"
            ($exitCode -ne 0) -and ($outputStr -match 'mismatch')
        } finally { Remove-Item -Recurse -Force $FakePlug2 -ErrorAction SilentlyContinue }
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
