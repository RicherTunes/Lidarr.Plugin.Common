#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs enforce-pr.ps1 against all plugin repos plus Common compliance checks.

.DESCRIPTION
    Orchestrates the full ecosystem promotion matrix described in
    docs/ECOSYSTEM_PROMOTION_CHECKLIST.md.  Produces a consolidated report
    showing per-repo results and the aggregate runtime test count.

.PARAMETER ReposRoot
    Parent directory containing all plugin repos and lidarr.plugin.common.
    Defaults to the parent of the Common repo root.

.PARAMETER SkipBuild
    Forward -SkipBuild to each enforce-pr.ps1 invocation.

.PARAMETER SkipTests
    Forward -SkipTests to each enforce-pr.ps1 invocation.

.EXAMPLE
    pwsh scripts/enforce-ecosystem.ps1 -ReposRoot D:\Alex\github
#>

[CmdletBinding()]
param(
    [string]$ReposRoot,
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Resolve paths ───────────────────────────────────────────────────────

$scriptDir = $PSScriptRoot
$enforcePrScript = Join-Path $scriptDir 'enforce-pr.ps1'

if (-not (Test-Path $enforcePrScript)) {
    Write-Host "ERROR: enforce-pr.ps1 not found at $enforcePrScript" -ForegroundColor Red
    exit 2
}

# Common repo root is one level up from scripts/
$commonRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path

if (-not $ReposRoot) {
    $ReposRoot = (Resolve-Path (Join-Path $commonRoot '..')).Path
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Ecosystem Promotion Matrix" -ForegroundColor Cyan
Write-Host " Date: $(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK')" -ForegroundColor Cyan
Write-Host " Root: $ReposRoot" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ── Known repos ─────────────────────────────────────────────────────────

# The four plugin repos (order: Brainarr, Tidalarr, Qobuzarr, AppleMusicarr)
$pluginRepos = @(
    @{ Name = 'brainarr';       Aliases = @('brainarr', 'Brainarr') }
    @{ Name = 'tidalarr';       Aliases = @('tidalarr', 'Tidalarr') }
    @{ Name = 'qobuzarr';       Aliases = @('qobuzarr', 'Qobuzarr') }
    @{ Name = 'applemusicarr';  Aliases = @('applemusicarr', 'AppleMusicarr') }
)

# ── State tracking ──────────────────────────────────────────────────────

$script:TotalFailures = 0
$script:RepoSummaries = @()
$script:TotalRuntimePassed = 0
$script:TotalRuntimeTotal = 0

function Resolve-RepoPath {
    param([hashtable]$Repo)
    foreach ($alias in $Repo.Aliases) {
        $path = Join-Path $ReposRoot $alias
        if (Test-Path $path) { return $path }
    }
    return $null
}

# ── 1. Common compliance ───────────────────────────────────────────────

Write-Host "--- Common Compliance ---" -ForegroundColor Yellow
$commonTestProject = Join-Path $commonRoot 'tests' 'Lidarr.Plugin.Common.Tests.csproj'

if (Test-Path $commonTestProject) {
    if ($SkipTests) {
        Write-Host "  Common compliance: SKIP (via -SkipTests)" -ForegroundColor DarkGray
        $script:RepoSummaries += @{
            Name = 'lidarr.plugin.common'
            Status = 'SKIP'
            Detail = 'Skipped via -SkipTests'
            RuntimePassed = 0
            RuntimeTotal = 0
        }
    } else {
        Write-Host "  Running Bridge + Compliance tests..." -ForegroundColor DarkGray
        $commonOutput = & dotnet test $commonTestProject `
            --filter "FullyQualifiedName~Bridge|FullyQualifiedName~Compliance" `
            --blame-hang-timeout 30s --nologo -m:1 2>&1
        $commonExitCode = $LASTEXITCODE

        $cPassed = 0; $cFailed = 0; $cSkipped = 0
        foreach ($line in $commonOutput) {
            if ($line -match 'Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)') {
                $cFailed = [int]$Matches[1]
                $cPassed = [int]$Matches[2]
                $cSkipped = [int]$Matches[3]
            } elseif ($line -match 'Passed:\s*(\d+)') {
                $cPassed = [int]$Matches[1]
            }
        }

        $cTotal = $cPassed + $cFailed
        $script:TotalRuntimePassed += $cPassed
        $script:TotalRuntimeTotal += $cTotal

        if ($commonExitCode -ne 0 -or $cFailed -gt 0) {
            Write-Host "  Common compliance: FAIL ($cPassed/$cTotal, $cFailed failed)" -ForegroundColor Red
            $script:TotalFailures++
            $script:RepoSummaries += @{
                Name = 'lidarr.plugin.common'
                Status = 'FAIL'
                Detail = "$cPassed/$cTotal passed ($cFailed failed)"
                RuntimePassed = $cPassed
                RuntimeTotal = $cTotal
            }
        } else {
            Write-Host "  Common compliance: PASS ($cPassed/$cTotal)" -ForegroundColor Green
            $script:RepoSummaries += @{
                Name = 'lidarr.plugin.common'
                Status = 'PASS'
                Detail = "$cPassed/$cTotal passed"
                RuntimePassed = $cPassed
                RuntimeTotal = $cTotal
            }
        }
    }
} else {
    Write-Host "  WARN: Common test project not found at $commonTestProject" -ForegroundColor Yellow
    $script:RepoSummaries += @{
        Name = 'lidarr.plugin.common'
        Status = 'WARN'
        Detail = 'Test project not found'
        RuntimePassed = 0
        RuntimeTotal = 0
    }
}

Write-Host ""

# ── 2. Plugin repos ────────────────────────────────────────────────────

foreach ($repo in $pluginRepos) {
    $repoPath = Resolve-RepoPath $repo
    if (-not $repoPath) {
        Write-Host "--- $($repo.Name): MISSING ---" -ForegroundColor Yellow
        Write-Host "  Not found in $ReposRoot — skipping." -ForegroundColor DarkGray
        $script:RepoSummaries += @{
            Name = $repo.Name
            Status = 'MISSING'
            Detail = 'Repo not found'
            RuntimePassed = 0
            RuntimeTotal = 0
        }
        Write-Host ""
        continue
    }

    Write-Host "--- $($repo.Name) ---" -ForegroundColor Yellow

    # Run enforce-pr.ps1 and capture output
    $forwardArgs = @('-RepoPath', $repoPath)
    if ($SkipBuild) { $forwardArgs += '-SkipBuild' }
    if ($SkipTests) { $forwardArgs += '-SkipTests' }

    $prOutput = & pwsh -NoProfile -File $enforcePrScript @forwardArgs 2>&1
    $prExitCode = $LASTEXITCODE

    # Display the output
    foreach ($line in $prOutput) {
        Write-Host "  $line"
    }

    # Extract runtime counts from the evidence block
    $rtPassed = 0; $rtTotal = 0
    foreach ($line in $prOutput) {
        $lineStr = $line.ToString()
        if ($lineStr -match 'Runtime:\s*(\w+)\s*\((\d+)/(\d+)') {
            $rtPassed = [int]$Matches[2]
            $rtTotal = [int]$Matches[3]
        } elseif ($lineStr -match 'Runtime:\s*PASS\s*\((\d+)/(\d+)\s+PASS\)') {
            $rtPassed = [int]$Matches[1]
            $rtTotal = [int]$Matches[2]
        }
    }

    $script:TotalRuntimePassed += $rtPassed
    $script:TotalRuntimeTotal += $rtTotal

    $status = if ($prExitCode -eq 0) { 'PASS' } else { 'FAIL' }
    if ($prExitCode -ne 0) { $script:TotalFailures++ }

    $script:RepoSummaries += @{
        Name = $repo.Name
        Status = $status
        Detail = "exit code $prExitCode"
        RuntimePassed = $rtPassed
        RuntimeTotal = $rtTotal
    }

    Write-Host ""
}

# ── Summary ─────────────────────────────────────────────────────────────

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Ecosystem Promotion Matrix — Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$maxNameLen = ($script:RepoSummaries | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum
if ($maxNameLen -lt 4) { $maxNameLen = 4 }

foreach ($summary in $script:RepoSummaries) {
    $pad = $maxNameLen - $summary.Name.Length + 2
    $dots = '.' * $pad
    $color = switch ($summary.Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'SKIP' { 'DarkGray' }
        'MISSING' { 'Yellow' }
        'WARN' { 'Yellow' }
        default { 'White' }
    }
    $rtStr = if ($summary.RuntimeTotal -gt 0) { " (runtime: $($summary.RuntimePassed)/$($summary.RuntimeTotal))" } else { '' }
    Write-Host "  $($summary.Name) $dots $($summary.Status)$rtStr" -ForegroundColor $color
}

Write-Host ""
Write-Host "  Runtime total: $script:TotalRuntimePassed / $script:TotalRuntimeTotal" -ForegroundColor Cyan
Write-Host ""

if ($script:TotalFailures -gt 0) {
    Write-Host "$($script:TotalFailures) repo(s) FAILED — see docs/MANUAL_ENFORCEMENT_RUNBOOK.md for remediation." -ForegroundColor Red
    exit 1
} else {
    Write-Host "All repos passed." -ForegroundColor Green
    exit 0
}
