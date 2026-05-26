#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Automated PR enforcement checks for a single Lidarr plugin repo.

.DESCRIPTION
    Wraps the manual enforcement runbook (docs/MANUAL_ENFORCEMENT_RUNBOOK.md) into
    a single script.  Run locally before merging any PR in billing-blocked repos.
    Produces a formatted evidence block ready to paste into a PR comment.

.PARAMETER RepoPath
    Path to the plugin repo root.  Defaults to the current directory.

.PARAMETER SkipBuild
    Skip the dotnet build step (useful when iterating on test-only changes).

.PARAMETER SkipTests
    Skip the full test suite (build and lint checks only).

.EXAMPLE
    # Run from a plugin repo root
    pwsh path/to/enforce-pr.ps1

    # Specify a repo explicitly
    pwsh path/to/enforce-pr.ps1 -RepoPath D:\Alex\github\tidalarr
#>

[CmdletBinding()]
param(
    [string]$RepoPath = (Get-Location).Path,
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Resolve and validate repo path ──────────────────────────────────────

$RepoPath = (Resolve-Path $RepoPath -ErrorAction Stop).Path

if (-not (Test-Path (Join-Path $RepoPath '*.sln'))) {
    Write-Host "ERROR: No .sln file found in $RepoPath — is this a plugin repo root?" -ForegroundColor Red
    exit 2
}

$repoName = Split-Path $RepoPath -Leaf
Write-Host ""
Write-Host "=== Enforcement Checks: $repoName ===" -ForegroundColor Cyan
Write-Host "    Path: $RepoPath"
Write-Host "    Date: $(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK')"
Write-Host ""

# ── State tracking ──────────────────────────────────────────────────────

$script:Failures = 0
$results = [ordered]@{}

function Record-Result {
    param(
        [string]$Name,
        [string]$Status,
        [string]$Detail
    )
    $results[$Name] = @{ Status = $Status; Detail = $Detail }
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'SKIP' { 'DarkGray' }
        'WARN' { 'Yellow' }
        default { 'White' }
    }
    Write-Host "  $Name ... $Status" -ForegroundColor $color
    if ($Detail) {
        Write-Host "      $Detail" -ForegroundColor DarkGray
    }
    if ($Status -eq 'FAIL') {
        $script:Failures++
    }
}

# ── Detect repo type ────────────────────────────────────────────────────

$isCommon = (Test-Path (Join-Path $RepoPath 'src' 'Lidarr.Plugin.Common.csproj')) -or
            ($repoName -match '(?i)common')

# ── 1. Build ────────────────────────────────────────────────────────────

if ($SkipBuild) {
    Record-Result -Name 'Build' -Status 'SKIP' -Detail 'Skipped via -SkipBuild'
} else {
    Write-Host "[1/6] Building..." -ForegroundColor DarkGray
    $buildOutput = & dotnet build -m:1 --nologo $RepoPath 2>&1
    $buildExitCode = $LASTEXITCODE
    $errorLines = $buildOutput | Where-Object { $_ -match ':\s+error\s+' }
    $errorCount = ($errorLines | Measure-Object).Count

    if ($buildExitCode -ne 0 -or $errorCount -gt 0) {
        Record-Result -Name 'Build' -Status 'FAIL' -Detail "$errorCount error(s), exit code $buildExitCode"
        if ($errorLines) {
            foreach ($line in $errorLines | Select-Object -First 5) {
                Write-Host "      $line" -ForegroundColor Red
            }
        }
    } else {
        Record-Result -Name 'Build' -Status 'PASS' -Detail '0 errors'
    }
}

# ── 2. Full test suite ─────────────────────────────────────────────────

if ($SkipTests) {
    Record-Result -Name 'Tests' -Status 'SKIP' -Detail 'Skipped via -SkipTests'
} else {
    Write-Host "[2/6] Running full test suite..." -ForegroundColor DarkGray
    $testOutput = & dotnet test --blame-hang-timeout 30s -m:1 --nologo $RepoPath 2>&1
    $testExitCode = $LASTEXITCODE

    # Parse test counts from dotnet test output
    $passed = 0; $failed = 0; $skipped = 0
    foreach ($line in $testOutput) {
        if ($line -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
        if ($line -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
        if ($line -match 'Skipped:\s*(\d+)') { $skipped = [int]$Matches[1] }
        # Also handle single-line summary: "Passed!  - Failed:     0, Passed:   171, Skipped:     4, Total:   175"
        if ($line -match 'Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)') {
            $failed = [int]$Matches[1]
            $passed = [int]$Matches[2]
            $skipped = [int]$Matches[3]
        }
    }

    $detail = "$passed passed, $failed failed, $skipped skipped"
    if ($testExitCode -ne 0 -or $failed -gt 0) {
        Record-Result -Name 'Tests' -Status 'FAIL' -Detail $detail
    } else {
        Record-Result -Name 'Tests' -Status 'PASS' -Detail $detail
    }
}

# ── 3. Runtime sandbox tests ───────────────────────────────────────────

if ($SkipTests) {
    Record-Result -Name 'Runtime' -Status 'SKIP' -Detail 'Skipped via -SkipTests'
} else {
    Write-Host "[3/6] Running runtime sandbox tests..." -ForegroundColor DarkGray
    $rtOutput = & dotnet test --filter "Category=Runtime" --blame-hang-timeout 30s -m:1 --nologo $RepoPath 2>&1
    $rtExitCode = $LASTEXITCODE

    $rtPassed = 0; $rtFailed = 0
    foreach ($line in $rtOutput) {
        if ($line -match 'Passed:\s*(\d+)') { $rtPassed = [int]$Matches[1] }
        if ($line -match 'Failed:\s*(\d+)') { $rtFailed = [int]$Matches[1] }
        if ($line -match 'Failed:\s+(\d+),\s+Passed:\s+(\d+)') {
            $rtFailed = [int]$Matches[1]
            $rtPassed = [int]$Matches[2]
        }
    }

    $rtTotal = $rtPassed + $rtFailed
    $detail = "$rtPassed/$rtTotal PASS"
    if ($rtExitCode -ne 0 -or $rtFailed -gt 0) {
        Record-Result -Name 'Runtime' -Status 'FAIL' -Detail "$rtPassed/$rtTotal ($rtFailed failed)"
    } elseif ($rtTotal -eq 0) {
        Record-Result -Name 'Runtime' -Status 'WARN' -Detail '0 runtime tests found'
    } else {
        Record-Result -Name 'Runtime' -Status 'PASS' -Detail $detail
    }
}

# ── 4. net6.0 regression check ─────────────────────────────────────────

Write-Host "[4/6] Checking for net6.0 regressions..." -ForegroundColor DarkGray
$net6FileGlobs = @('*.csproj', '*.props', '*.ps1', '*.sh', '*.yml')
$net6SearchPattern = 'net6' + '.0'  # split to avoid self-match
# Use relative paths for exclusion so parent directories (e.g., .claude worktrees) do not cause false positives
$net6Matches = @()
foreach ($glob in $net6FileGlobs) {
    $files = Get-ChildItem -Path $RepoPath -Filter $glob -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $rel = $_.FullName.Substring($RepoPath.Length)
            $rel -notmatch '[\\/](ext|obj|bin|\.git|\.claude)[\\/]' -and
            $_.Name -notmatch '^enforce-'  # exclude this script family
        }
    foreach ($file in $files) {
        $hits = Select-String -Path $file.FullName -Pattern $net6SearchPattern -SimpleMatch
        if ($hits) {
            $net6Matches += $hits
        }
    }
}

$net6Count = ($net6Matches | Measure-Object).Count
# Common is allowed up to 2 matches in tooling (per runbook); plugins expect 0
$net6Threshold = if ($isCommon) { 2 } else { 0 }
if ($net6Count -gt $net6Threshold) {
    Record-Result -Name 'net6.0' -Status 'FAIL' -Detail "$net6Count match(es) (allowed $net6Threshold)"
    foreach ($match in $net6Matches | Select-Object -First 5) {
        Write-Host "      $($match.Path):$($match.LineNumber): $($match.Line.Trim())" -ForegroundColor Red
    }
} else {
    $suffix = if ($net6Count -gt 0) { " ($net6Count in tooling, allowed)" } else { '' }
    Record-Result -Name 'net6.0' -Status 'PASS' -Detail "0 regressions$suffix"
}

# ── 5. Single IPlugin check ────────────────────────────────────────────

Write-Host "[5/6] Checking IPlugin count..." -ForegroundColor DarkGray
$srcDir = Join-Path $RepoPath 'src'
if (-not (Test-Path $srcDir)) {
    # Some repos use a different layout — search from repo root but exclude ext/obj/bin
    $srcDir = $RepoPath
}

$iPluginMatches = Get-ChildItem -Path $srcDir -Filter '*.cs' -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $rel = $_.FullName.Substring($srcDir.Length)
        $rel -notmatch '[\\/](ext|obj|bin|\.git|\.claude|tests?|Tests?)[\\/]'
    } |
    ForEach-Object {
        Select-String -Path $_.FullName -Pattern 'class\s+\w+\s*:.*IPlugin' |
            Where-Object { $_.Line -notmatch '(internal|abstract|//)' }
    }

$iPluginCount = ($iPluginMatches | Measure-Object).Count

if ($isCommon) {
    Record-Result -Name 'IPlugin' -Status 'SKIP' -Detail 'Common library — not a plugin repo'
} elseif ($iPluginCount -eq 1) {
    Record-Result -Name 'IPlugin' -Status 'PASS' -Detail "1 match"
} elseif ($iPluginCount -eq 0) {
    Record-Result -Name 'IPlugin' -Status 'FAIL' -Detail '0 matches — expected exactly 1'
} else {
    Record-Result -Name 'IPlugin' -Status 'FAIL' -Detail "$iPluginCount matches — expected exactly 1"
    foreach ($match in $iPluginMatches) {
        Write-Host "      $($match.Path):$($match.LineNumber): $($match.Line.Trim())" -ForegroundColor Red
    }
}

# ── 6. Common submodule tag check ──────────────────────────────────────

Write-Host "[6/6] Checking Common submodule..." -ForegroundColor DarkGray
$commonSubmodule = Join-Path $RepoPath 'ext' 'Lidarr.Plugin.Common'
if ($isCommon) {
    Record-Result -Name 'Common' -Status 'SKIP' -Detail 'This IS the Common repo'
} elseif (Test-Path $commonSubmodule) {
    $prevDir = Get-Location
    try {
        Set-Location $commonSubmodule
        $tagOutput = & git describe --tags --exact-match HEAD 2>&1
        $tagExitCode = $LASTEXITCODE
        if ($tagExitCode -eq 0) {
            $tag = ($tagOutput | Select-Object -First 1).ToString().Trim()
            Record-Result -Name 'Common' -Status 'PASS' -Detail $tag
        } else {
            $sha = (& git rev-parse --short HEAD 2>&1).ToString().Trim()
            Record-Result -Name 'Common' -Status 'FAIL' -Detail "HEAD ($sha) is not a tagged release"
        }
    } finally {
        Set-Location $prevDir
    }
} else {
    Record-Result -Name 'Common' -Status 'SKIP' -Detail 'No ext/Lidarr.Plugin.Common found'
}

# ── Evidence block ──────────────────────────────────────────────────────

Write-Host ""
Write-Host "=== Enforcement Check Results ===" -ForegroundColor Cyan
foreach ($key in $results.Keys) {
    $r = $results[$key]
    $statusStr = $r.Status
    $detailStr = if ($r.Detail) { " ($($r.Detail))" } else { '' }
    $color = switch ($r.Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'SKIP' { 'DarkGray' }
        'WARN' { 'Yellow' }
        default { 'White' }
    }
    Write-Host "${key}: ${statusStr}${detailStr}" -ForegroundColor $color
}
Write-Host "=================================" -ForegroundColor Cyan
Write-Host ""

# ── Pasteable evidence (plain text) ─────────────────────────────────────

$evidence = @()
$evidence += "=== Enforcement Check Results ==="
foreach ($key in $results.Keys) {
    $r = $results[$key]
    $detailStr = if ($r.Detail) { " ($($r.Detail))" } else { '' }
    $evidence += "${key}: $($r.Status)${detailStr}"
}
$evidence += "================================="

$fence = '`' * 3
Write-Host "Pasteable evidence block:" -ForegroundColor DarkGray
Write-Host $fence
$evidence | ForEach-Object { Write-Host $_ }
Write-Host $fence
Write-Host ""

# ── Exit code ───────────────────────────────────────────────────────────

if ($script:Failures -gt 0) {
    Write-Host "$($script:Failures) check(s) FAILED — see docs/MANUAL_ENFORCEMENT_RUNBOOK.md for remediation." -ForegroundColor Red
    exit 1
} else {
    Write-Host "All checks passed." -ForegroundColor Green
    exit 0
}
