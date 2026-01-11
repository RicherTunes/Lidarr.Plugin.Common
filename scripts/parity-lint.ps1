#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Parity lint for plugin repos - detects forbidden re-inventions.
.DESCRIPTION
    Scans plugin repos (Qobuzarr, Tidalarr, Brainarr) for patterns that should
    use shared library utilities instead of being reimplemented.
.PARAMETER RepoPath
    Path to plugin repo to scan.
.PARAMETER AllRepos
    Scan all known plugin repos.
.PARAMETER Fix
    Show suggested fixes for violations.
.PARAMETER Mode
    Run mode: 'interactive' (default) or 'ci' (strict, fails on expired baselines).
#>

param(
    [string]$RepoPath,
    [switch]$AllRepos,
    [switch]$Fix,
    [ValidateSet('interactive', 'ci')]
    [string]$Mode = 'interactive'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Known tech debt baseline - violations here are tracked but don't fail
# Each entry has: Key (repo:file:pattern), Owner, Expiry (YYYY-MM-DD), IssueUrl
$script:KnownTechDebt = @(
    @{
        Key = 'qobuzarr:main_pluginhost.cs:GetInvalidFileNameChars'
        Owner = 'alex'
        Expiry = '2026-06-01'
        IssueUrl = 'https://github.com/RicherTunes/Qobuzarr/issues/TBD'
    }
    @{
        Key = 'qobuzarr:QobuzCLI/Models/Configuration/DuplicateHandlingConfig.cs:GetInvalidFileNameChars'
        Owner = 'alex'
        Expiry = '2026-06-01'
        IssueUrl = 'https://github.com/RicherTunes/Qobuzarr/issues/TBD'
    }
    @{
        Key = 'qobuzarr:QobuzCLI/Services/PluginHost.cs:GetInvalidFileNameChars'
        Owner = 'alex'
        Expiry = '2026-06-01'
        IssueUrl = 'https://github.com/RicherTunes/Qobuzarr/issues/TBD'
    }
    @{
        Key = 'qobuzarr:src/Configuration/QobuzPluginConstants.cs:FLACMagicBytes'
        Owner = 'alex'
        Expiry = '2026-06-01'
        IssueUrl = 'https://github.com/RicherTunes/Qobuzarr/issues/TBD'
    }
    @{
        Key = 'tidalarr:src/Tidalarr/Integration/LidarrNative/TidalLidarrDownloadClient.cs:GetInvalidFileNameChars'
        Owner = 'alex'
        Expiry = '2026-06-01'
        IssueUrl = 'https://github.com/RicherTunes/Tidalarr/issues/TBD'
    }
    @{
        Key = 'tidalarr:TidalCLI/TidalCLIHelper.cs:GetInvalidFileNameChars'
        Owner = 'alex'
        Expiry = '2026-06-01'
        IssueUrl = 'https://github.com/RicherTunes/Tidalarr/issues/TBD'
    }
    # Test files - magic bytes needed for test data (acceptable)
    @{
        Key = 'qobuzarr:tests/Qobuzarr.Tests/Unit/Utilities/AudioMagicBytesValidatorTests.cs:FLACMagicBytes'
        Owner = 'alex'
        Expiry = '2099-12-31'
        IssueUrl = 'N/A - test data'
    }
    @{
        Key = 'qobuzarr:tests/Qobuzarr.Tests/Unit/Utilities/AudioMagicBytesValidatorTests.cs:OggMagicBytes'
        Owner = 'alex'
        Expiry = '2099-12-31'
        IssueUrl = 'N/A - test data'
    }
    @{
        Key = 'qobuzarr:tests/Qobuzarr.Tests/Unit/Utilities/AudioMagicBytesValidatorTests.cs:ID3MagicBytes'
        Owner = 'alex'
        Expiry = '2099-12-31'
        IssueUrl = 'N/A - test data'
    }
    @{
        Key = 'qobuzarr:tests/Qobuzarr.Tests/Unit/Utilities/TrackFileNameBuilderTests.cs:GetInvalidFileNameChars'
        Owner = 'alex'
        Expiry = '2099-12-31'
        IssueUrl = 'N/A - test data'
    }
)

# Banned patterns with full context
$script:BannedPatterns = @(
    @{
        Name = 'GetInvalidFileNameChars'
        Why = 'Platform-specific invalid chars cause cross-platform bugs'
        Use = 'Lidarr.Plugin.Common.Security.Sanitize.PathSegment()'
        Link = 'src/Security/Sanitize.cs'
        Pattern = 'Path\.GetInvalidFileNameChars\s*\(\s*\)'
        Severity = 'error'
    }
    @{
        Name = 'FLACMagicBytes'
        Why = 'Hardcoded magic bytes diverge from shared validator'
        Use = 'DownloadPayloadValidator.LooksLikeAudioPayload()'
        Link = 'src/Validation/DownloadPayloadValidator.cs'
        Pattern = '0x66\s*,\s*0x4C\s*,\s*0x61\s*,\s*0x43'
        Severity = 'error'
    }
    @{
        Name = 'OggMagicBytes'
        Why = 'Hardcoded magic bytes diverge from shared validator'
        Use = 'DownloadPayloadValidator.LooksLikeAudioPayload()'
        Link = 'src/Validation/DownloadPayloadValidator.cs'
        Pattern = '0x4F\s*,\s*0x67\s*,\s*0x67\s*,\s*0x53'
        Severity = 'error'
    }
    @{
        Name = 'ID3MagicBytes'
        Why = 'Hardcoded magic bytes diverge from shared validator'
        Use = 'DownloadPayloadValidator.LooksLikeAudioPayload()'
        Link = 'src/Validation/DownloadPayloadValidator.cs'
        Pattern = '0x49\s*,\s*0x44\s*,\s*0x33'
        Severity = 'error'
    }
)

# Directories to exclude from scanning
$script:ExcludeDirs = @('ext', 'docs', 'scripts', 'bin', 'obj', '.git', '.worktrees', 'node_modules')

function Normalize-Path {
    param([string]$Path)
    return $Path -replace '\\', '/'
}

function Test-IsInLineComment {
    <#
    .SYNOPSIS
        Conservative comment detection: only skips // line comments.
    .DESCRIPTION
        Checks if the match occurs after a // comment marker on the same line.
        To avoid false negatives on "http://" or regex patterns, only treats //
        as a comment if preceded by whitespace or at line start.
    #>
    param([string]$Content, [int]$MatchIndex)
    $beforeMatch = $Content.Substring(0, $MatchIndex)
    $lastNewline = $beforeMatch.LastIndexOf("`n")
    $lineStart = if ($lastNewline -ge 0) { $lastNewline + 1 } else { 0 }
    $lineContent = $Content.Substring($lineStart, $MatchIndex - $lineStart)

    $commentIndex = $lineContent.IndexOf('//')
    if ($commentIndex -lt 0) { return $false }

    # Only treat as comment if // is at line start or preceded by whitespace
    # This avoids false negatives on "http://", "file://", regex patterns, etc.
    if ($commentIndex -eq 0) { return $true }
    $charBefore = $lineContent[$commentIndex - 1]
    return [char]::IsWhiteSpace($charBefore)
}

function Test-IsExcludedPath {
    <#
    .SYNOPSIS
        Excludes build artifacts and generated files only.
    .DESCRIPTION
        Scans ALL test files - parity violations in tests are just as bad.
        Only excludes: bin/, obj/, ext/, docs/, scripts/, .git/, generated files.
    #>
    param([string]$RelPath)
    $normalized = Normalize-Path $RelPath
    $parts = $normalized -split '/'
    foreach ($part in $parts) {
        if ($part -in $script:ExcludeDirs) { return $true }
    }
    # Only exclude generated files (*.g.cs)
    if ($normalized -match '\.g\.cs$') { return $true }
    return $false
}

function Get-BaselineEntry {
    param([string]$Repo, [string]$File, [string]$Pattern)
    $normalizedFile = Normalize-Path $File
    $key = "${Repo}:${normalizedFile}:${Pattern}".ToLowerInvariant()
    foreach ($entry in $script:KnownTechDebt) {
        $entryKey = $entry.Key.ToLowerInvariant()
        if ($key -like "*$entryKey*" -or $entryKey -like "*$key*") { return $entry }
    }
    return $null
}

function Test-BaselineExpired {
    param([hashtable]$Entry)
    if (-not $Entry.Expiry) { return $false }
    try {
        # Use invariant culture and UTC to avoid timezone flakiness
        $expiryDate = [DateTime]::ParseExact($Entry.Expiry, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
        $todayUtc = [DateTime]::UtcNow.Date
        return $todayUtc -gt $expiryDate
    } catch { return $false }
}

function Get-PluginRepos {
    param([string]$CommonRoot)
    $parent = Split-Path $CommonRoot -Parent
    $repos = @()
    foreach ($name in @('qobuzarr', 'tidalarr', 'brainarr')) {
        $path = Join-Path $parent $name
        $capPath = Join-Path $parent ($name.Substring(0,1).ToUpper() + $name.Substring(1))
        if (Test-Path $path) { $repos += @{ Name = $name; Path = $path } }
        elseif (Test-Path $capPath) { $repos += @{ Name = $name; Path = $capPath } }
    }
    return $repos
}

function Find-Violations {
    param([string]$RepoPath, [string]$RepoName)
    $violations = @()
    $files = Get-ChildItem -Path $RepoPath -Filter '*.cs' -Recurse -File -ErrorAction SilentlyContinue
    foreach ($file in $files) {
        $rel = $file.FullName.Substring($RepoPath.Length).TrimStart('\', '/')
        $relNormalized = Normalize-Path $rel
        if (Test-IsExcludedPath $rel) { continue }
        try {
            $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
            if (-not $content) { continue }
            foreach ($p in $script:BannedPatterns) {
                $matches = [regex]::Matches($content, $p.Pattern)
                foreach ($m in $matches) {
                    if (Test-IsInLineComment -Content $content -MatchIndex $m.Index) { continue }
                    $lineNum = ($content.Substring(0, $m.Index) -split "`n").Count
                    $violations += [PSCustomObject]@{
                        Repo = $RepoName
                        File = $relNormalized
                        Line = $lineNum
                        Pattern = $p.Name
                        Why = $p.Why
                        Use = $p.Use
                        Link = $p.Link
                        Severity = $p.Severity
                    }
                }
            }
        } catch { }
    }
    # Sort for deterministic output (repo, file, pattern)
    return $violations | Sort-Object Repo, File, Pattern
}

# Main
$script:ShowFix = $Fix
$script:IsCIMode = ($Mode -eq 'ci')
$commonRoot = Split-Path $PSScriptRoot -Parent

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Parity Lint - Plugin Re-invention Check" -ForegroundColor Cyan
if ($script:IsCIMode) { Write-Host "Mode: CI (strict)" -ForegroundColor Yellow }
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$reposToScan = @()
if ($RepoPath) {
    if (-not (Test-Path $RepoPath)) { Write-Host "ERROR: Path not found: $RepoPath" -ForegroundColor Red; exit 2 }
    $reposToScan += @{ Name = (Split-Path $RepoPath -Leaf); Path = $RepoPath }
} elseif ($AllRepos) {
    $reposToScan = Get-PluginRepos -CommonRoot $commonRoot
    if ($reposToScan.Count -eq 0) { Write-Host "No repos found" -ForegroundColor Yellow; exit 0 }
} else {
    Write-Host "Usage: parity-lint.ps1 [-RepoPath <path>] [-AllRepos] [-Fix] [-Mode ci|interactive]"
    exit 0
}

$allNew = @()
$expiredBaselines = @()

foreach ($repo in $reposToScan) {
    Write-Host "Scanning: $($repo.Name)" -ForegroundColor Cyan
    $violations = @(Find-Violations -RepoPath $repo.Path -RepoName $repo.Name)
    $new = @(); $baselined = 0; $expired = @()
    foreach ($v in $violations) {
        $entry = Get-BaselineEntry -Repo $v.Repo -File $v.File -Pattern $v.Pattern
        if ($entry) {
            if (Test-BaselineExpired $entry) {
                $expired += [PSCustomObject]@{ Violation = $v; Entry = $entry }
            }
            $baselined++
        } else { $new += $v }
    }
    if ($new.Count -eq 0 -and $baselined -eq 0) {
        Write-Host "  [OK] No violations" -ForegroundColor Green
    } elseif ($new.Count -eq 0 -and $expired.Count -eq 0) {
        Write-Host "  [OK] $baselined baselined (tech debt)" -ForegroundColor DarkGray
    } else {
        if ($new.Count -gt 0) {
            Write-Host "  Found $($new.Count) NEW violation(s):" -ForegroundColor Red
            foreach ($v in $new) {
                Write-Host "    [X] $($v.File):$($v.Line) - $($v.Pattern)" -ForegroundColor Red
                Write-Host "        WHY:  $($v.Why)" -ForegroundColor Yellow
                Write-Host "        USE:  $($v.Use)" -ForegroundColor Green
                Write-Host "        LINK: $($v.Link)" -ForegroundColor Cyan
            }
            $allNew += $new
        }
        if ($expired.Count -gt 0) {
            Write-Host "  Found $($expired.Count) EXPIRED baseline(s):" -ForegroundColor Magenta
            foreach ($e in $expired) {
                Write-Host "    [!] $($e.Violation.File):$($e.Violation.Line) - $($e.Violation.Pattern)" -ForegroundColor Magenta
                Write-Host "        Owner:  $($e.Entry.Owner)" -ForegroundColor Yellow
                Write-Host "        Expiry: $($e.Entry.Expiry) (EXPIRED)" -ForegroundColor Red
                Write-Host "        Issue:  $($e.Entry.IssueUrl)" -ForegroundColor Cyan
            }
            $expiredBaselines += $expired
        }
    }
    Write-Host ""
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary: Repos=$($reposToScan.Count), New=$($allNew.Count), Expired=$($expiredBaselines.Count)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$exitCode = 0
if ($allNew.Count -gt 0) {
    Write-Host "FAILED: $($allNew.Count) new violation(s)" -ForegroundColor Red
    $exitCode = 1
}
if ($expiredBaselines.Count -gt 0 -and $script:IsCIMode) {
    Write-Host "FAILED: $($expiredBaselines.Count) expired baseline(s) in CI mode" -ForegroundColor Red
    $exitCode = 1
}
if ($exitCode -eq 0) { Write-Host "PASSED" -ForegroundColor Green }
exit $exitCode
