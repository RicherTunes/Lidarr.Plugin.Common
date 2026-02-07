#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Lint source files for sync-over-async patterns (.GetAwaiter().GetResult()).
.DESCRIPTION
    Scans src/**/*.cs for .GetAwaiter().GetResult() calls and fails if any are found
    outside allowlisted files. Supports diff-aware mode for PRs (only flags new
    occurrences) and strict mode for main branch (flags all non-allowlisted).
.PARAMETER Path
    Root of the repo to scan (default: current directory).
.PARAMETER Mode
    'interactive' (default) warns; 'ci' fails hard on violations.
.PARAMETER AllowlistPath
    Path to sync-over-async-allowlist.json (default: .github/sync-over-async-allowlist.json under Path).
.PARAMETER DiffBase
    Git ref to diff against for PR mode. When set, only new occurrences in the diff are flagged.
    Unset = strict mode (all non-allowlisted matches fail).
.PARAMETER SelfTest
    Run built-in fixture tests to validate detection and allowlist logic.
#>

param(
    [string]$Path = '.',
    [ValidateSet('interactive', 'ci')]
    [string]$Mode = 'interactive',
    [string]$AllowlistPath,
    [string]$DiffBase,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:IsCIMode = ($Mode -eq 'ci')
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
    param([string]$RelativePath, [array]$Allowlist)
    $normalized = $RelativePath -replace '\\', '/'
    foreach ($entry in $Allowlist) {
        $pattern = $entry.path -replace '\\', '/'
        # Support glob-style wildcards
        if ($pattern.Contains('*')) {
            $regex = '^' + [regex]::Escape($pattern).Replace('\*\*', '.*').Replace('\*', '[^/]*') + '$'
            if ($normalized -match $regex) { return $entry }
        } elseif ($normalized -eq $pattern -or $normalized.EndsWith("/$pattern") -or $normalized.EndsWith("\$pattern")) {
            return $entry
        }
    }
    return $null
}

# ─── Diff-Aware Detection ────────────────────────────────────────────────────

function Get-DiffAddedLines {
    param([string]$Base, [string]$RepoRoot)
    $added = @{}
    try {
        $diff = git -C $RepoRoot diff "$Base...HEAD" --unified=0 --diff-filter=ACMR -- 'src/**/*.cs' 2>$null
        $currentFile = $null
        foreach ($line in $diff) {
            if ($line -match '^\+\+\+ b/(.+)$') {
                $currentFile = $Matches[1]
            } elseif ($line -match '^\+[^+]' -and $currentFile) {
                if (-not $added.ContainsKey($currentFile)) {
                    $added[$currentFile] = [System.Collections.Generic.List[string]]::new()
                }
                $added[$currentFile].Add($line.Substring(1))
            }
        }
    } catch {
        Write-Warning "Failed to get diff: $_"
    }
    return $added
}

# ─── Core Scan ───────────────────────────────────────────────────────────────

function Invoke-Scan {
    param(
        [string]$RepoRoot,
        [array]$Allowlist,
        [hashtable]$DiffLines
    )

    $violations = @()
    $suppressed = @()
    $srcPath = Join-Path $RepoRoot 'src'

    if (-not (Test-Path $srcPath)) {
        Write-Warning "No src/ directory found at $RepoRoot"
        return @{ Violations = $violations; Suppressed = $suppressed }
    }

    $files = Get-ChildItem -Path $srcPath -Filter '*.cs' -Recurse -File
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
        $lines = @(Get-Content $file.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $script:Pattern) {
                $lineNum = $i + 1
                $match = @{
                    File = $relativePath
                    Line = $lineNum
                    Content = $lines[$i].Trim()
                }

                # Check allowlist
                $allowEntry = Test-Allowlisted $relativePath $Allowlist
                if ($allowEntry) {
                    $suppressed += $match
                    continue
                }

                # In diff mode, only flag new occurrences
                if ($DiffLines -and $DiffLines.Count -gt 0) {
                    $diffFile = $relativePath -replace '\\', '/'
                    if (-not $DiffLines.ContainsKey($diffFile)) { continue }
                    $isNew = $false
                    foreach ($addedLine in $DiffLines[$diffFile]) {
                        if ($addedLine -match $script:Pattern) {
                            $isNew = $true
                            break
                        }
                    }
                    if (-not $isNew) { continue }
                }

                $violations += $match
            }
        }
    }

    return @{ Violations = $violations; Suppressed = $suppressed }
}

# ─── Self-Test ───────────────────────────────────────────────────────────────

function Invoke-SelfTest {
    $passed = 0
    $failed = 0
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "lint-soa-test-$(Get-Random)"
    $srcDir = Join-Path $tempDir 'src'
    New-Item -ItemType Directory -Path $srcDir -Force | Out-Null

    try {
        # Test 1: Detect .GetAwaiter().GetResult()
        $testFile = Join-Path $srcDir 'Bad.cs'
        Set-Content $testFile 'var x = task.GetAwaiter().GetResult();'
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @() -DiffLines @{}
        if ($result.Violations.Count -eq 1) {
            Write-Host '  [PASS] Test 1: Detects .GetAwaiter().GetResult()' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 1: Expected 1 violation, got $($result.Violations.Count)" -ForegroundColor Red
            $failed++
        }

        # Test 2: Allowlisted file is suppressed
        $allowlist = @(@{ path = 'src/Bad.cs'; reason = 'test'; category = 'A' })
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist $allowlist -DiffLines @{}
        if ($result.Violations.Count -eq 0 -and $result.Suppressed.Count -eq 1) {
            Write-Host '  [PASS] Test 2: Allowlisted file is suppressed' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 2: Expected 0 violations + 1 suppressed, got $($result.Violations.Count) + $($result.Suppressed.Count)" -ForegroundColor Red
            $failed++
        }

        # Test 3: Clean file has no violations
        $cleanFile = Join-Path $srcDir 'Clean.cs'
        Set-Content $cleanFile 'var x = await task;'
        Remove-Item $testFile -Force
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @() -DiffLines @{}
        if ($result.Violations.Count -eq 0) {
            Write-Host '  [PASS] Test 3: Clean file has no violations' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 3: Expected 0 violations, got $($result.Violations.Count)" -ForegroundColor Red
            $failed++
        }

        # Test 4: Wildcard allowlist pattern
        $testFile2 = Join-Path $srcDir 'Integration'
        New-Item -ItemType Directory -Path $testFile2 -Force | Out-Null
        $testFile2 = Join-Path $testFile2 'Adapter.cs'
        Set-Content $testFile2 'var x = task.GetAwaiter().GetResult();'
        $allowlist = @(@{ path = 'src/Integration/*'; reason = 'host contract'; category = 'A' })
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist $allowlist -DiffLines @{}
        if ($result.Violations.Count -eq 0 -and $result.Suppressed.Count -eq 1) {
            Write-Host '  [PASS] Test 4: Wildcard allowlist suppresses match' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 4: Expected 0 violations + 1 suppressed, got $($result.Violations.Count) + $($result.Suppressed.Count)" -ForegroundColor Red
            $failed++
        }

        # Test 5: Multiline .GetAwaiter()\n.GetResult() (less common but valid)
        Remove-Item $testFile2 -Force
        $multiFile = Join-Path $srcDir 'Multi.cs'
        Set-Content $multiFile "var x = task.GetAwaiter()`n    .GetResult();"
        $result = Invoke-Scan -RepoRoot $tempDir -Allowlist @() -DiffLines @{}
        # This is a single-line regex so multiline won't match — that's OK, it catches the common pattern
        if ($result.Violations.Count -eq 0) {
            Write-Host '  [PASS] Test 5: Multiline split not flagged (by design — single-line scan)' -ForegroundColor Green
            $passed++
        } else {
            Write-Host "  [FAIL] Test 5: Expected 0 violations for multiline split" -ForegroundColor Red
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

$allowlist = Read-Allowlist $AllowlistPath
$diffLines = @{}
if ($DiffBase) {
    Write-Host "Diff-aware mode: comparing against $DiffBase" -ForegroundColor Cyan
    $diffLines = Get-DiffAddedLines -Base $DiffBase -RepoRoot $resolvedPath
}

$result = Invoke-Scan -RepoRoot $resolvedPath -Allowlist $allowlist -DiffLines $diffLines

# ─── Output ──────────────────────────────────────────────────────────────────

if ($result.Suppressed.Count -gt 0) {
    Write-Host "Allowlisted ($($result.Suppressed.Count)):" -ForegroundColor DarkGray
    foreach ($s in $result.Suppressed) {
        Write-Host "  $($s.File):$($s.Line) — $($s.Content)" -ForegroundColor DarkGray
    }
}

if ($result.Violations.Count -eq 0) {
    Write-Host "No sync-over-async violations found." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "Sync-over-async violations ($($result.Violations.Count)):" -ForegroundColor Red
foreach ($v in $result.Violations) {
    $msg = "$($v.File):$($v.Line) — $($v.Content)"
    if ($script:IsCIMode) {
        Write-Host "::error file=$($v.File),line=$($v.Line)::$($v.Content)"
    }
    Write-Host "  $msg" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "To allowlist a file, add it to $AllowlistPath with category and reason." -ForegroundColor Cyan
Write-Host 'Categories: A = host-required sync, B = avoidable, C = test/helper' -ForegroundColor Cyan

if ($script:IsCIMode) {
    exit 1
} else {
    Write-Warning "Found $($result.Violations.Count) violation(s). Use -Mode ci to fail hard."
    exit 0
}
