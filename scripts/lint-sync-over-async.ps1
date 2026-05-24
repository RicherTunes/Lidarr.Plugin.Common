#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Lint source files for sync-over-async patterns (.GetAwaiter().GetResult()).
.DESCRIPTION
    Scans <SrcDir>/**/*.cs for .GetAwaiter().GetResult() (error) and
    .Result/.Wait() (warn-only) patterns, validating findings against
    a JSON allowlist.  Three modes:

      ci    — Strict: fails on ANY non-allowlisted match (main branch)
      pr    — Diff-aware: fails only on NEW matches introduced in this PR
      local — Same as ci but with verbose output (default)

    POLICY: Only Category A (host-init — Lidarr forces synchronous entry
    points) may be allowlisted.  Category B (avoidable) must be converted
    to async/await.  Category C (test-only) is out of scope (tests/ not
    scanned).
.PARAMETER Path
    Root of the repo to scan (default: current directory).
.PARAMETER Mode
    'local' (default) warns verbosely; 'ci' fails hard; 'pr' is diff-aware.
.PARAMETER AllowlistPath
    Path to sync-over-async-allowlist.json
    (default: .github/sync-over-async-allowlist.json under Path).
.PARAMETER SrcDir
    Subdirectory within Path to scan (default: src).
    Use this for plugins whose source root is not src/ (e.g., Brainarr.Plugin).
.PARAMETER BaseBranch
    Base branch for diff-aware PR mode (default: origin/main).
.PARAMETER SelfTest
    Run built-in fixture tests to validate detection and allowlist logic.
.EXAMPLE
    # Default — scans src/ under current directory
    pwsh -File lint-sync-over-async.ps1

    # Brainarr layout — source root is Brainarr.Plugin/ not src/
    pwsh -File lint-sync-over-async.ps1 -Path . -SrcDir Brainarr.Plugin

    # CI strict mode on main
    pwsh -File lint-sync-over-async.ps1 -Mode ci

    # CI diff-aware on pull request
    pwsh -File lint-sync-over-async.ps1 -Mode pr -BaseBranch origin/main
#>

param(
    [string]$Path = '.',
    [ValidateSet('interactive', 'ci', 'pr', 'local')]
    [string]$Mode = 'local',
    [string]$AllowlistPath,
    [string]$SrcDir = 'src',   # per-plugin override (e.g., Brainarr.Plugin)
    [string]$BaseBranch = 'origin/main',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Normalise: 'interactive' is an alias for 'local' kept for back-compat
if ($Mode -eq 'interactive') { $Mode = 'local' }

$script:Pattern = '\.GetAwaiter\(\)\s*\.GetResult\(\)'

# ─── Allowlist ───────────────────────────────────────────────────────────────

function Read-Allowlist {
    param([string]$FilePath)
    if (-not $FilePath -or -not (Test-Path $FilePath)) { return @() }
    $json = Get-Content $FilePath -Raw | ConvertFrom-Json
    if (-not $json.entries) { return @() }
    return @($json.entries)
}

function Test-Allowlisted {
    param([string]$RelativePath, [int]$LineNum, [array]$Allowlist)
    $normalized = $RelativePath -replace '\\', '/'
    # Line-number tolerance: allowlist line may drift ±10 lines from recorded value.
    $LineTolerance = 10
    foreach ($entry in $Allowlist) {
        # Support both 'file' (deployed allowlists) and 'path' (legacy) keys.
        # Hashtables (self-tests) use .ContainsKey; PSCustomObject (JSON) uses .PSObject.Properties.
        $rawPath = $null
        $entryLine = $null
        if ($entry -is [hashtable]) {
            if ($entry.ContainsKey('file')) { $rawPath = $entry['file'] }
            elseif ($entry.ContainsKey('path')) { $rawPath = $entry['path'] }
            if ($entry.ContainsKey('line')) { $entryLine = $entry['line'] }
        } else {
            if ($entry.PSObject.Properties.Match('file').Count -gt 0) { $rawPath = $entry.file }
            elseif ($entry.PSObject.Properties.Match('path').Count -gt 0) { $rawPath = $entry.path }
            if ($entry.PSObject.Properties.Match('line').Count -gt 0) { $entryLine = $entry.line }
        }
        if (-not $rawPath) { continue }
        $pattern = $rawPath -replace '\\', '/'

        # Path matching: exact, trailing, or glob wildcard
        $pathMatch = $false
        if ($pattern.Contains('*')) {
            $regex = '^' + [regex]::Escape($pattern).Replace('\*\*', '.*').Replace('\*', '[^/]*') + '$'
            $pathMatch = $normalized -match $regex
        } else {
            $pathMatch = ($normalized -eq $pattern) -or
                         ($normalized.EndsWith("/$pattern")) -or
                         ($normalized.EndsWith($pattern))
        }
        if (-not $pathMatch) { continue }

        # Line-number check (if the allowlist entry has a line number)
        if ($null -ne $entryLine -and $entryLine -gt 0) {
            if ([Math]::Abs($LineNum - [int]$entryLine) -gt $LineTolerance) { continue }
        }

        return $entry
    }
    return $null
}

# ─── Diff-Aware Detection ────────────────────────────────────────────────────

function Get-DiffAddedLines {
    param([string]$Base, [string]$RepoRoot, [string]$ScanPath)
    $diffNewLines = @{}
    try {
        $diff = git -C $RepoRoot diff "$Base...HEAD" -- $ScanPath 2>$null
        $currentFile = $null
        $newLineNum  = 0

        foreach ($dline in $diff) {
            if ($dline -match '^diff --git a/.+ b/(.+)$') {
                $currentFile = $Matches[1]
                $newLineNum  = 0
            }
            elseif ($dline -match '^@@ -\d+(?:,\d+)? \+(\d+)') {
                $newLineNum = [int]$Matches[1] - 1
            }
            elseif ($null -ne $currentFile) {
                if ($dline.StartsWith('+') -and -not $dline.StartsWith('+++')) {
                    $newLineNum++
                    if ($dline -match $script:Pattern) {
                        $key = "$currentFile`:$newLineNum"
                        $diffNewLines[$key] = $true
                    }
                }
                elseif (-not ($dline.StartsWith('-') -and -not $dline.StartsWith('---'))) {
                    $newLineNum++
                }
            }
        }
    } catch {
        Write-Warning "Failed to get diff: $_"
    }
    return $diffNewLines
}

# ─── Core Scan ───────────────────────────────────────────────────────────────

function Invoke-Scan {
    param(
        [string]$RepoRoot,
        [string]$SrcPath,
        [array]$Allowlist,
        [hashtable]$DiffLines
    )

    $violations = @()
    $suppressed = @()
    $repoRootNorm = $RepoRoot.Replace('\', '/').TrimEnd('/')

    if (-not (Test-Path $SrcPath)) {
        Write-Warning "No source directory found at $SrcPath"
        return @{ Violations = $violations; Suppressed = $suppressed }
    }

    $files = Get-ChildItem -Path $SrcPath -Filter '*.cs' -Recurse -File
    foreach ($file in $files) {
        $fullNorm = $file.FullName.Replace('\', '/')
        $relativePath = if ($fullNorm.StartsWith("$repoRootNorm/")) {
            $fullNorm.Substring($repoRootNorm.Length + 1)
        } else {
            $file.FullName.TrimStart('\', '/')
        }

        $lines = @(Get-Content $file.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $script:Pattern) {
                $lineNum = $i + 1
                $match = @{
                    File    = $relativePath
                    Line    = $lineNum
                    Content = $lines[$i].Trim()
                }

                # Check allowlist (with line-number tolerance)
                $allowEntry = Test-Allowlisted $relativePath $lineNum $Allowlist
                if ($allowEntry) {
                    if ($Mode -eq 'local') {
                        $cat = if ($allowEntry -is [hashtable]) { $allowEntry['category'] } else { $allowEntry.category }
                        $rsn = if ($allowEntry -is [hashtable]) { $allowEntry['reason'] } else { $allowEntry.reason }
                        Write-Host "  [ALLOW] $($relativePath):$lineNum — cat=$cat`: $rsn" -ForegroundColor DarkGray
                    }
                    $suppressed += $match
                    continue
                }

                # In diff mode, only flag new occurrences
                if ($Mode -eq 'pr' -and $DiffLines -and $DiffLines.Count -gt 0) {
                    $key = "$($relativePath -replace '\\', '/'):$lineNum"
                    if (-not $DiffLines.ContainsKey($key)) { continue }
                }

                $violations += $match
            }
        }
    }

    return @{ Violations = $violations; Suppressed = $suppressed }
}

# ─── Warn-Only Scan (.Result / .Wait()) ──────────────────────────────────────

function Invoke-WarnScan {
    param([string]$SrcPath, [string]$RepoRoot)

    $repoRootNorm = $RepoRoot.Replace('\', '/').TrimEnd('/')
    $WarnPatterns = @(
        @{ Regex = '\.Result\b'; Label = '.Result' },
        @{ Regex = '\.Wait\(\)';  Label = '.Wait()' }
    )

    $totalWarnings = 0
    foreach ($wp in $WarnPatterns) {
        $warnFindings = @()
        Get-ChildItem -Path $SrcPath -Filter '*.cs' -Recurse -File | ForEach-Object {
            $fullNorm = $_.FullName.Replace('\', '/')
            $relPath  = if ($fullNorm.StartsWith("$repoRootNorm/")) {
                $fullNorm.Substring($repoRootNorm.Length + 1)
            } else { $fullNorm }

            $lineNum = 0
            foreach ($line in (Get-Content $_.FullName)) {
                $lineNum++
                if ($line -match $wp.Regex) {
                    # Skip comments and string literals (rough heuristic)
                    $trimmed = $line.TrimStart()
                    if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('*') -or $trimmed.StartsWith('///')) { continue }
                    $warnFindings += [PSCustomObject]@{
                        File    = $relPath
                        Line    = $lineNum
                        Content = $line.Trim()
                    }
                }
            }
        }

        if ($warnFindings.Count -gt 0) {
            Write-Host ""
            Write-Host "=== WARN: $($wp.Label) occurrences ($($warnFindings.Count)) ===" -ForegroundColor Yellow
            foreach ($w in $warnFindings) {
                $loc = "$($w.File):$($w.Line)"
                Write-Host "  $loc : $($w.Content)" -ForegroundColor Yellow
                if ($Mode -ne 'local') {
                    Write-Host "::warning file=$($w.File),line=$($w.Line)::Sync-over-async (warn): $($w.Content)"
                }
            }
            $totalWarnings += $warnFindings.Count
        }
    }

    if ($totalWarnings -gt 0) {
        Write-Host ""
        Write-Host "[WARN] $totalWarnings .Result/.Wait() occurrence(s) found (non-blocking)" -ForegroundColor Yellow
    }
}

# ─── Self-Test ───────────────────────────────────────────────────────────────

function Invoke-SelfTest {
    $passed = 0
    $failed = 0
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "lint-soa-test-$(Get-Random)"
    $srcSubDir = Join-Path $tempDir 'src'
    New-Item -ItemType Directory -Path $srcSubDir -Force | Out-Null

    try {
        # Test 1: Detect .GetAwaiter().GetResult()
        $testFile = Join-Path $srcSubDir 'Bad.cs'
        Set-Content $testFile 'var x = task.GetAwaiter().GetResult();'
        $result = Invoke-Scan -RepoRoot $tempDir -SrcPath $srcSubDir -Allowlist @() -DiffLines @{}
        if ($result.Violations.Count -eq 1) {
            Write-Host '  [PASS] Test 1: Detects .GetAwaiter().GetResult()' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 1: Expected 1 violation, got $($result.Violations.Count)" -ForegroundColor Red
            $failed++
        }

        # Test 2: Allowlisted file is suppressed (no line required)
        $allowlist = @(@{ file = 'src/Bad.cs'; reason = 'test'; category = 'A' })
        $result = Invoke-Scan -RepoRoot $tempDir -SrcPath $srcSubDir -Allowlist $allowlist -DiffLines @{}
        if ($result.Violations.Count -eq 0 -and $result.Suppressed.Count -eq 1) {
            Write-Host '  [PASS] Test 2: Allowlisted file is suppressed' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 2: Expected 0 violations + 1 suppressed, got $($result.Violations.Count) + $($result.Suppressed.Count)" -ForegroundColor Red
            $failed++
        }

        # Test 3: Clean file has no violations
        $cleanFile = Join-Path $srcSubDir 'Clean.cs'
        Set-Content $cleanFile 'var x = await task;'
        Remove-Item $testFile -Force
        $result = Invoke-Scan -RepoRoot $tempDir -SrcPath $srcSubDir -Allowlist @() -DiffLines @{}
        if ($result.Violations.Count -eq 0) {
            Write-Host '  [PASS] Test 3: Clean file has no violations' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 3: Expected 0 violations, got $($result.Violations.Count)" -ForegroundColor Red
            $failed++
        }

        # Test 4: Wildcard allowlist pattern
        $intDir = Join-Path $srcSubDir 'Integration'
        New-Item -ItemType Directory -Path $intDir -Force | Out-Null
        $testFile2 = Join-Path $intDir 'Adapter.cs'
        Set-Content $testFile2 'var x = task.GetAwaiter().GetResult();'
        $allowlist = @(@{ file = 'src/Integration/*'; reason = 'host contract'; category = 'A' })
        $result = Invoke-Scan -RepoRoot $tempDir -SrcPath $srcSubDir -Allowlist $allowlist -DiffLines @{}
        if ($result.Violations.Count -eq 0 -and $result.Suppressed.Count -eq 1) {
            Write-Host '  [PASS] Test 4: Wildcard allowlist suppresses match' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 4: Expected 0 violations + 1 suppressed, got $($result.Violations.Count) + $($result.Suppressed.Count)" -ForegroundColor Red
            $failed++
        }

        # Test 5: Line-number tolerance on allowlist (entry at line 5, match at line 1 — within ±10)
        Remove-Item $testFile2 -Force
        $tolFile = Join-Path $srcSubDir 'Tol.cs'
        Set-Content $tolFile 'var x = task.GetAwaiter().GetResult();'
        $allowlist = @(@{ file = 'src/Tol.cs'; line = 5; reason = 'tolerance test'; category = 'A' })
        $result = Invoke-Scan -RepoRoot $tempDir -SrcPath $srcSubDir -Allowlist $allowlist -DiffLines @{}
        if ($result.Violations.Count -eq 0 -and $result.Suppressed.Count -eq 1) {
            Write-Host '  [PASS] Test 5: Line-number tolerance (±10) suppresses match' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 5: Expected 0 violations + 1 suppressed, got $($result.Violations.Count) + $($result.Suppressed.Count)" -ForegroundColor Red
            $failed++
        }

        # Test 6: Custom SrcDir — scan a non-src directory
        $customSrcDir = Join-Path $tempDir 'CustomPlugin'
        New-Item -ItemType Directory -Path $customSrcDir -Force | Out-Null
        $customFile = Join-Path $customSrcDir 'Feature.cs'
        Set-Content $customFile 'var x = task.GetAwaiter().GetResult();'
        $result = Invoke-Scan -RepoRoot $tempDir -SrcPath $customSrcDir -Allowlist @() -DiffLines @{}
        if ($result.Violations.Count -eq 1) {
            Write-Host '  [PASS] Test 6: Custom SrcDir scans non-src directory' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 6: Expected 1 violation from custom SrcDir, got $($result.Violations.Count)" -ForegroundColor Red
            $failed++
        }

        Write-Host ""
        Write-Host "Self-test: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
        return $failed -eq 0
    } finally {
        Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ─── Main ────────────────────────────────────────────────────────────────────

if ($SelfTest) {
    Write-Host 'Running self-tests...' -ForegroundColor Cyan
    $ok = Invoke-SelfTest
    exit $(if ($ok) { 0 } else { 1 })
}

$resolvedPath = Resolve-Path $Path
if (-not $AllowlistPath) {
    $AllowlistPath = Join-Path $resolvedPath '.github' 'sync-over-async-allowlist.json'
}

# Resolve the scan directory using SrcDir
$srcPath = Join-Path $resolvedPath $SrcDir

$allowlist = Read-Allowlist $AllowlistPath
if (Test-Path $AllowlistPath) {
    Write-Host "Loaded $($allowlist.Count) allowlist entries from $(Split-Path $AllowlistPath -Leaf)"
} else {
    Write-Host "No allowlist found at $AllowlistPath (all matches = violations)"
}

$diffLines = @{}
if ($Mode -eq 'pr') {
    Write-Host "PR mode: filtering to newly-added violations only (base: $BaseBranch)"
    try {
        $diffLines = Get-DiffAddedLines -Base $BaseBranch -RepoRoot $resolvedPath -ScanPath $srcPath
    } catch {
        Write-Host "::warning::Could not compute diff against $BaseBranch — falling back to strict mode"
        $diffLines = @{}
        $Mode = 'ci'
    }
}

$result = Invoke-Scan -RepoRoot $resolvedPath -SrcPath $srcPath -Allowlist $allowlist -DiffLines $diffLines

Write-Host "Found $($result.Violations.Count + $result.Suppressed.Count) sync-over-async occurrence(s) in $SrcDir/"

# ─── Output ──────────────────────────────────────────────────────────────────

if ($result.Suppressed.Count -gt 0 -and $Mode -ne 'local') {
    Write-Host "Allowlisted ($($result.Suppressed.Count)):" -ForegroundColor DarkGray
    foreach ($s in $result.Suppressed) {
        Write-Host "  $($s.File):$($s.Line) — $($s.Content)" -ForegroundColor DarkGray
    }
}

if ($result.Violations.Count -gt 0) {
    Write-Host ""
    Write-Host "=== SYNC-OVER-ASYNC VIOLATIONS ===" -ForegroundColor Red

    foreach ($v in $result.Violations) {
        $loc = "$($v.File):$($v.Line)"
        Write-Host "  $loc : $($v.Content)" -ForegroundColor Red
        if ($Mode -ne 'local') {
            Write-Host "::error file=$($v.File),line=$($v.Line)::Sync-over-async: $($v.Content)"
        }
    }

    Write-Host ""
    Write-Host "To fix: convert to async/await."
    Write-Host "If Category A (host forces sync entry-point), add to allowlist:"
    Write-Host "  $AllowlistPath"
    Write-Host 'Categories: A = host-required sync, B = avoidable, C = test/helper' -ForegroundColor Cyan
}

# ─── Warn-only scan: .Result / .Wait() ───────────────────────────────────────
# These patterns MAY indicate sync-over-async but also have legitimate uses.
# Warn only - never fail CI.
if (Test-Path $srcPath) {
    Invoke-WarnScan -SrcPath $srcPath -RepoRoot $resolvedPath
}

if ($result.Violations.Count -eq 0) {
    Write-Host ""
    Write-Host "[OK] No sync-over-async violations" -ForegroundColor Green
    exit 0
}

if ($Mode -eq 'ci' -or $Mode -eq 'pr') {
    exit 1
} else {
    Write-Warning "Found $($result.Violations.Count) violation(s). Use -Mode ci to fail hard."
    exit 0
}
