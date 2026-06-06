#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Lint source files for culture-unsafe date parsing and unguarded Unix-epoch conversions (LOOP-007 / #18).
.DESCRIPTION
    Two rule categories, scanned over src/**/*.cs:

      CULTURE  — (DateTime|DateTimeOffset).(Try)Parse(Exact) / Convert.ToDateTime WITHOUT
                 CultureInfo.InvariantCulture on the same call. A non-invariant culture can shift the
                 parsed instant (e.g. non-Gregorian calendars, locale month names). Route through
                 Common's TimeParsing.TryParseIsoDateInvariant, or pass CultureInfo.InvariantCulture.

      EPOCH    — raw DateTimeOffset.FromUnixTime{Seconds,Milliseconds}. These THROW
                 ArgumentOutOfRangeException on out-of-range (hostile/garbage) values, crashing the
                 pipeline. Route through Common's TimeParsing.TryFromUnixTime{Seconds,Milliseconds}
                 which fail closed. The canonical wrapper (TimeParsing.cs) is allowlisted.

    The ecosystem is currently clean (every parse already uses InvariantCulture, every epoch call is
    guarded), so this gate is a REGRESSION BARRIER: it keeps the bug class from reappearing in new code.

    Escape hatches: a trailing '// lint:allow-date' comment on the offending line, or a whole-file
    entry in the JSON allowlist.
.PARAMETER Path
    Root of the repo to scan (default: current directory).
.PARAMETER Mode
    'interactive' (default) warns; 'ci' fails hard (exit 1) on violations.
.PARAMETER AllowlistPath
    Path to date-parsing-allowlist.json (default: .github/date-parsing-allowlist.json under Path).
.PARAMETER SelfTest
    Run built-in fixture tests to validate detection and allowlist logic.
#>

param(
    [string]$Path = '.',
    [ValidateSet('interactive', 'ci')]
    [string]$Mode = 'interactive',
    [string]$AllowlistPath,
    [string]$SourceDir,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:IsCIMode = ($Mode -eq 'ci')

# CULTURE: a parse/convert call. Flagged only when InvariantCulture is absent from the same line.
$script:CulturePattern = '(?:DateTime|DateTimeOffset)\s*\.\s*(?:TryParse|Parse|TryParseExact|ParseExact)\s*\(|Convert\s*\.\s*ToDateTime\s*\('
# EPOCH: raw FromUnixTime* conversion (use TimeParsing.TryFromUnixTime* instead).
$script:EpochPattern = 'DateTimeOffset\s*\.\s*FromUnixTime(?:Seconds|Milliseconds)\s*\('
$script:AllowComment = '//\s*lint:allow-date'

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
        $rawPath = $null
        if ($entry -is [hashtable]) {
            if ($entry.ContainsKey('file')) { $rawPath = $entry['file'] }
            elseif ($entry.ContainsKey('path')) { $rawPath = $entry['path'] }
        } else {
            if ($entry.PSObject.Properties.Match('file').Count -gt 0) { $rawPath = $entry.file }
            elseif ($entry.PSObject.Properties.Match('path').Count -gt 0) { $rawPath = $entry.path }
        }
        if (-not $rawPath) { continue }
        $pattern = $rawPath -replace '\\', '/'
        if ($pattern.Contains('*')) {
            $regex = '^' + [regex]::Escape($pattern).Replace('\*\*', '.*').Replace('\*', '[^/]*') + '$'
            if ($normalized -match $regex) { return $entry }
        } elseif ($normalized -eq $pattern -or $normalized.EndsWith("/$pattern")) {
            return $entry
        }
    }
    return $null
}

# ─── Core Scan ───────────────────────────────────────────────────────────────

# Resolve the source directory to scan. Explicit -SourceDir wins; otherwise try 'src' (most plugins +
# Common), then a '*.Plugin' directory (Brainarr's layout: Brainarr.Plugin/). Returns $null if none found.
function Resolve-SourceDir {
    param([string]$RepoRoot, [string]$Explicit)
    if ($Explicit) {
        $p = Join-Path $RepoRoot $Explicit
        return (Test-Path $p) ? $p : $null
    }
    $src = Join-Path $RepoRoot 'src'
    if (Test-Path $src) { return $src }
    $plugin = Get-ChildItem -Path $RepoRoot -Directory -Filter '*.Plugin' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($plugin) { return $plugin.FullName }
    return $null
}

function Invoke-Scan {
    param([string]$RepoRoot, [array]$Allowlist, [string]$SourceDir)

    $violations = @()
    $suppressed = @()
    $srcPath = Resolve-SourceDir -RepoRoot $RepoRoot -Explicit $SourceDir
    if (-not $srcPath -or -not (Test-Path $srcPath)) {
        Write-Warning "No source directory found at $RepoRoot (tried 'src' and '*.Plugin')"
        return @{ Violations = $violations; Suppressed = $suppressed }
    }

    $files = Get-ChildItem -Path $srcPath -Filter '*.cs' -Recurse -File
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
        $lines = @(Get-Content $file.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]

            # Skip comment lines — doc/block comments routinely mention these APIs in prose.
            $trimmed = $line.TrimStart()
            if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('*') -or $trimmed.StartsWith('/*')) { continue }

            $rule = $null
            if ($line -match $script:CulturePattern) {
                # A parse call's arguments can span several lines, so InvariantCulture may sit on a
                # continuation line. Scan a small forward window (the argument span) before flagging.
                $window = $line
                for ($j = $i + 1; $j -lt [Math]::Min($i + 4, $lines.Count); $j++) { $window += "`n" + $lines[$j] }
                if ($window -notmatch 'InvariantCulture') { $rule = 'CULTURE' }
            }
            elseif ($line -match $script:EpochPattern) { $rule = 'EPOCH' }
            if (-not $rule) { continue }

            $match = @{ File = $relativePath; Line = $i + 1; Content = $line.Trim(); Rule = $rule }

            # Inline escape comment
            if ($line -match $script:AllowComment) { $suppressed += $match; continue }

            # Whole-file allowlist
            if (Test-Allowlisted $relativePath $Allowlist) { $suppressed += $match; continue }

            $violations += $match
        }
    }
    return @{ Violations = $violations; Suppressed = $suppressed }
}

# ─── Self-Test ───────────────────────────────────────────────────────────────

function Invoke-SelfTest {
    $passed = 0; $failed = 0
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "lint-date-test-$(Get-Random)"
    $srcDir = Join-Path $tempDir 'src'
    New-Item -ItemType Directory -Path $srcDir -Force | Out-Null

    function Assert-Count($name, $actual, $expected) {
        if ($actual -eq $expected) { Write-Host "  [PASS] $name" -ForegroundColor Green; return 1 }
        Write-Host "  [FAIL] $name (expected $expected, got $actual)" -ForegroundColor Red; return 0
    }

    try {
        $f = Join-Path $srcDir 'T.cs'

        # 1: culture-unsafe DateTime.Parse flagged
        Set-Content $f 'var d = DateTime.Parse(s);'
        $r = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $passed += ($t = Assert-Count 'Test 1: culture-unsafe DateTime.Parse flagged' $r.Violations.Count 1); if (-not $t) { $failed++ }

        # 2: InvariantCulture on same line is clean
        Set-Content $f 'var ok = DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d);'
        $r = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $passed += ($t = Assert-Count 'Test 2: InvariantCulture parse is clean' $r.Violations.Count 0); if (-not $t) { $failed++ }

        # 3: raw FromUnixTimeSeconds flagged
        Set-Content $f 'var dt = DateTimeOffset.FromUnixTimeSeconds(v);'
        $r = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $passed += ($t = Assert-Count 'Test 3: raw FromUnixTimeSeconds flagged' $r.Violations.Count 1); if (-not $t) { $failed++ }

        # 4: FromUnixTimeMilliseconds flagged + rule label is EPOCH
        Set-Content $f 'var dt = DateTimeOffset.FromUnixTimeMilliseconds(v);'
        $r = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $isEpoch = ($r.Violations.Count -eq 1 -and $r.Violations[0].Rule -eq 'EPOCH')
        $passed += ($t = Assert-Count 'Test 4: FromUnixTimeMilliseconds labelled EPOCH' $isEpoch $true); if (-not $t) { $failed++ }

        # 5: TimeParsing.TryFromUnixTimeSeconds (the wrapper call) is NOT flagged
        Set-Content $f 'if (TimeParsing.TryFromUnixTimeSeconds(v, out var dt)) { }'
        $r = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $passed += ($t = Assert-Count 'Test 5: TimeParsing.TryFromUnixTimeSeconds is clean' $r.Violations.Count 0); if (-not $t) { $failed++ }

        # 6: inline // lint:allow-date suppresses
        Set-Content $f 'var dt = DateTimeOffset.FromUnixTimeSeconds(v); // lint:allow-date canonical wrapper'
        $r = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $ok = ($r.Violations.Count -eq 0 -and $r.Suppressed.Count -eq 1)
        $passed += ($t = Assert-Count 'Test 6: inline allow-date suppresses' $ok $true); if (-not $t) { $failed++ }

        # 7: whole-file allowlist suppresses
        Set-Content $f 'var dt = DateTimeOffset.FromUnixTimeSeconds(v);'
        $r = Invoke-Scan -RepoRoot $tempDir -Allowlist @(@{ file = 'src/T.cs'; reason = 'canonical wrapper' })
        $ok = ($r.Violations.Count -eq 0 -and $r.Suppressed.Count -eq 1)
        $passed += ($t = Assert-Count 'Test 7: whole-file allowlist suppresses' $ok $true); if (-not $t) { $failed++ }

        # 8: Convert.ToDateTime flagged
        Set-Content $f 'var d = Convert.ToDateTime(s);'
        $r = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $passed += ($t = Assert-Count 'Test 8: Convert.ToDateTime flagged' $r.Violations.Count 1); if (-not $t) { $failed++ }

        # 9: ordinary code is clean (no false positives)
        Set-Content $f 'var now = DateTime.UtcNow.AddSeconds(expiresIn); var s = now.ToString("o");'
        $r = Invoke-Scan -RepoRoot $tempDir -Allowlist @()
        $passed += ($t = Assert-Count 'Test 9: UtcNow.AddSeconds / ToString not flagged' $r.Violations.Count 0); if (-not $t) { $failed++ }

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
    exit $(if (Invoke-SelfTest) { 0 } else { 1 })
}

$resolvedPath = Resolve-Path $Path
if (-not $AllowlistPath) {
    $AllowlistPath = Join-Path $resolvedPath '.github' 'date-parsing-allowlist.json'
}

$allowlist = Read-Allowlist $AllowlistPath
$result = Invoke-Scan -RepoRoot $resolvedPath -Allowlist $allowlist -SourceDir $SourceDir

if ($result.Suppressed.Count -gt 0) {
    Write-Host "Suppressed (allowlisted) date-parsing sites: $($result.Suppressed.Count)" -ForegroundColor DarkGray
}

if ($result.Violations.Count -eq 0) {
    Write-Host "[OK] No culture-unsafe date parsing or unguarded epoch conversions found." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "Found $($result.Violations.Count) date-parsing violation(s):" -ForegroundColor $(if ($script:IsCIMode) { 'Red' } else { 'Yellow' })
foreach ($v in $result.Violations) {
    Write-Host "  [$($v.Rule)] $($v.File):$($v.Line)" -ForegroundColor $(if ($script:IsCIMode) { 'Red' } else { 'Yellow' })
    Write-Host "      $($v.Content)" -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "CULTURE -> pass CultureInfo.InvariantCulture, or use TimeParsing.TryParseIsoDateInvariant." -ForegroundColor Cyan
Write-Host "EPOCH   -> use TimeParsing.TryFromUnixTime{Seconds,Milliseconds} (fails closed on bad input)." -ForegroundColor Cyan
Write-Host "Justified exception: append '// lint:allow-date <reason>' or add a file entry to the allowlist." -ForegroundColor Cyan

exit $(if ($script:IsCIMode) { 1 } else { 0 })
