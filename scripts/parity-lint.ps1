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
#>

param(
    [string]$RepoPath,
    [switch]$AllRepos,
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Known tech debt baseline - violations here are tracked but don't fail
$script:KnownTechDebt = @(
    'qobuzarr:main_pluginhost.cs:GetInvalidFileNameChars'
    'qobuzarr:QobuzCLI\Models\Configuration\DuplicateHandlingConfig.cs:GetInvalidFileNameChars'
    'qobuzarr:QobuzCLI\Services\PluginHost.cs:GetInvalidFileNameChars'
    'qobuzarr:src\Configuration\QobuzPluginConstants.cs:FLACMagicBytes'
    'tidalarr:src\Tidalarr\Integration\LidarrNative\TidalLidarrDownloadClient.cs:GetInvalidFileNameChars'
    'tidalarr:TidalCLI\TidalCLIHelper.cs:GetInvalidFileNameChars'
)

# Banned patterns - specific to avoid false positives
$script:BannedPatterns = @(
    @{
        Name = 'GetInvalidFileNameChars'
        Description = 'Using Path.GetInvalidFileNameChars() (use Sanitize from Common)'
        Pattern = 'Path\.GetInvalidFileNameChars\s*\(\s*\)'
        Severity = 'error'
        Fix = 'Use Lidarr.Plugin.Common.Security.Sanitize.PathSegment()'
    }
    @{
        Name = 'FLACMagicBytes'
        Description = 'Hardcoded FLAC magic bytes (use DownloadPayloadValidator)'
        Pattern = '0x66\s*,\s*0x4C\s*,\s*0x61\s*,\s*0x43'
        Severity = 'error'
        Fix = 'Use DownloadPayloadValidator.LooksLikeAudioPayload()'
    }
    @{
        Name = 'OggMagicBytes'
        Description = 'Hardcoded OggS magic bytes (use DownloadPayloadValidator)'
        Pattern = '0x4F\s*,\s*0x67\s*,\s*0x67\s*,\s*0x53'
        Severity = 'error'
        Fix = 'Use DownloadPayloadValidator.LooksLikeAudioPayload()'
    }
    @{
        Name = 'ID3MagicBytes'
        Description = 'Hardcoded ID3 magic bytes (use DownloadPayloadValidator)'
        Pattern = '0x49\s*,\s*0x44\s*,\s*0x33'
        Severity = 'error'
        Fix = 'Use DownloadPayloadValidator.LooksLikeAudioPayload()'
    }
)

function Test-IsBaselined {
    param([string]$Repo, [string]$File, [string]$Pattern)
    $repoLower = $Repo.ToLowerInvariant()
    foreach ($b in $script:KnownTechDebt) {
        $bLower = $b.ToLowerInvariant()
        if ("${repoLower}:${File}:${Pattern}".ToLowerInvariant() -like "*$bLower*") {
            return $true
        }
    }
    return $false
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
        # Skip ext/, tests, worktrees
        if ($rel -like 'ext*' -or $rel -like '*Test*' -or $rel -like '*.worktrees*') { continue }

        try {
            $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
            if (-not $content) { continue }

            foreach ($p in $script:BannedPatterns) {
                $matches = [regex]::Matches($content, $p.Pattern)
                foreach ($m in $matches) {
                    $lineNum = ($content.Substring(0, $m.Index) -split "`n").Count
                    $violations += [PSCustomObject]@{
                        Repo = $RepoName
                        File = $rel
                        Line = $lineNum
                        Pattern = $p.Name
                        Description = $p.Description
                        Severity = $p.Severity
                        Fix = $p.Fix
                    }
                }
            }
        } catch { }
    }
    return $violations
}

# Main
$script:ShowFix = $Fix
$commonRoot = Split-Path $PSScriptRoot -Parent

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Parity Lint - Plugin Re-invention Check" -ForegroundColor Cyan
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
    Write-Host "Usage: parity-lint.ps1 [-RepoPath <path>] [-AllRepos] [-Fix]"
    exit 0
}

$allNew = @()
foreach ($repo in $reposToScan) {
    Write-Host "Scanning: $($repo.Name)" -ForegroundColor Cyan
    $violations = @(Find-Violations -RepoPath $repo.Path -RepoName $repo.Name)

    $new = @(); $baselined = 0
    foreach ($v in $violations) {
        if (Test-IsBaselined -Repo $v.Repo -File $v.File -Pattern $v.Pattern) { $baselined++ }
        else { $new += $v }
    }

    if ($new.Count -eq 0 -and $baselined -eq 0) {
        Write-Host "  [OK] No violations" -ForegroundColor Green
    } elseif ($new.Count -eq 0) {
        Write-Host "  [OK] $baselined baselined (tech debt)" -ForegroundColor DarkGray
    } else {
        Write-Host "  Found $($new.Count) NEW violation(s):" -ForegroundColor Red
        foreach ($v in $new) {
            Write-Host "    [X] $($v.File):$($v.Line) - $($v.Pattern)" -ForegroundColor Red
            if ($script:ShowFix) { Write-Host "        Fix: $($v.Fix)" -ForegroundColor Green }
        }
        $allNew += $new
    }
    Write-Host ""
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary: Repos=$($reposToScan.Count), New errors=$($allNew.Count)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($allNew.Count -gt 0) {
    Write-Host "FAILED: $($allNew.Count) new violation(s)" -ForegroundColor Red
    exit 1
}
Write-Host "PASSED" -ForegroundColor Green
exit 0
