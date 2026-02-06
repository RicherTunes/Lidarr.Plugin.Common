#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Lint workflow files for unpinned reusable workflow references.
.DESCRIPTION
    Scans .github/workflows/*.yml for `uses:` lines referencing
    RicherTunes/Lidarr.Plugin.Common/ and fails if not pinned by full 40-char SHA.
    Third-party actions are out of scope.
.PARAMETER Path
    Root of the repo to scan (default: current directory).
.PARAMETER Mode
    'interactive' (default) warns; 'ci' fails hard on violations and expired allowlist entries.
.PARAMETER AllowlistPath
    Path to sha-pin-allowlist.json (default: .github/sha-pin-allowlist.json under Path).
.PARAMETER SelfTest
    Run built-in fixture tests to validate regex and allowlist logic.
#>

param(
    [string]$Path = '.',
    [ValidateSet('interactive', 'ci')]
    [string]$Mode = 'interactive',
    [string]$AllowlistPath,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:IsCIMode = ($Mode -eq 'ci')
$script:CommonRepoPattern = 'RicherTunes/Lidarr\.Plugin\.Common/'
$script:ShaRegex = '^[0-9a-fA-F]{40}$'

# ─── Allowlist ───────────────────────────────────────────────────────────────

function Read-Allowlist {
    param([string]$FilePath)
    if (-not $FilePath -or -not (Test-Path $FilePath)) { return @() }
    $json = Get-Content $FilePath -Raw | ConvertFrom-Json
    if (-not $json.entries) { return @() }
    return @($json.entries)
}

function Find-AllowlistMatch {
    param([string]$File, [string]$RepoPath, [array]$Allowlist)
    $basename = [System.IO.Path]::GetFileName($File)
    foreach ($entry in $Allowlist) {
        if ($entry.file -eq $basename -and $entry.repoPath -eq $RepoPath) {
            return $entry
        }
    }
    return $null
}

function Get-EntryProperty {
    param([object]$Entry, [string]$Name)
    if ($Entry.PSObject.Properties.Match($Name).Count -gt 0) { return $Entry.$Name }
    return $null
}

function Test-AllowlistEntryValid {
    <#
    .SYNOPSIS
        Validates allowlist entry governance fields. Returns $true if entry suppresses the violation.
    #>
    param([object]$Entry, [bool]$CIMode)
    $problems = @()

    $owner = Get-EntryProperty $Entry 'owner'
    $expiresOn = Get-EntryProperty $Entry 'expiresOn'

    if (-not $owner) {
        $problems += 'missing owner'
    }
    if (-not $expiresOn) {
        $problems += 'missing expiresOn'
    } else {
        try {
            $expiry = [DateTime]::ParseExact($expiresOn, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
            if ([DateTime]::UtcNow.Date -gt $expiry) {
                $problems += "expired ($expiresOn)"
            }
        } catch {
            $problems += "invalid expiresOn format: $expiresOn"
        }
    }

    if ($problems.Count -eq 0) { return @{ Valid = $true; Problems = @() } }

    return @{ Valid = (-not $CIMode); Problems = $problems }
}

# ─── Scanning ────────────────────────────────────────────────────────────────

function Find-Violations {
    <#
    .SYNOPSIS
        Scans workflow YAML files for unpinned Common workflow references.
    .OUTPUTS
        Array of violation objects with File, Line, UsesLine, RepoPath, Ref properties.
    #>
    param([string]$RepoRoot)

    $workflowDir = Join-Path $RepoRoot '.github' 'workflows'
    if (-not (Test-Path $workflowDir)) {
        Write-Host "  No .github/workflows/ found at $RepoRoot" -ForegroundColor Yellow
        return @()
    }

    $violations = @()
    [array]$yamlFiles = @(Get-ChildItem -Path $workflowDir -Filter '*.yml' -File -ErrorAction SilentlyContinue) +
                        @(Get-ChildItem -Path $workflowDir -Filter '*.yaml' -File -ErrorAction SilentlyContinue)

    foreach ($file in $yamlFiles) {
        $lines = Get-Content $file.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            # Match uses: lines (job-level reusable workflows and step-level actions)
            if ($line -match '^\s*-?\s*uses:\s+(\S+)@(\S+)') {
                $fullRef = $Matches[1]
                $ref = $Matches[2]

                # Only lint Common repo references
                if ($fullRef -notmatch $script:CommonRepoPattern) { continue }

                # Check if ref is a full 40-char SHA
                if ($ref -notmatch $script:ShaRegex) {
                    $violations += [PSCustomObject]@{
                        File     = $file.Name
                        FilePath = $file.FullName
                        Line     = $i + 1
                        UsesLine = $line.Trim()
                        RepoPath = $fullRef
                        Ref      = $ref
                    }
                }
            }
        }
    }

    return $violations
}

# ─── Main ────────────────────────────────────────────────────────────────────

function Invoke-Lint {
    param(
        [string]$RepoRoot,
        [string]$AllowlistFile,
        [bool]$CIMode
    )

    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Workflow SHA Pin Lint" -ForegroundColor Cyan
    if ($CIMode) { Write-Host "Mode: CI (strict)" -ForegroundColor Yellow }
    Write-Host "Scanning: $RepoRoot" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""

    [array]$allowlist = @(Read-Allowlist $AllowlistFile)
    if ($allowlist.Count -gt 0) {
        Write-Host "  Allowlist: $AllowlistFile ($($allowlist.Count) entries)" -ForegroundColor DarkGray
    }

    $violations = @(Find-Violations -RepoRoot $RepoRoot)

    if ($violations.Count -eq 0) {
        Write-Host "  [OK] All Common workflow refs are SHA-pinned" -ForegroundColor Green
        Write-Host ""
        Write-Host "PASSED" -ForegroundColor Green
        return 0
    }

    $newViolations = @()
    $suppressedCount = 0
    $failedAllowlist = @()

    foreach ($v in $violations) {
        $entry = Find-AllowlistMatch -File $v.File -RepoPath $v.RepoPath -Allowlist $allowlist
        if ($entry) {
            $check = Test-AllowlistEntryValid -Entry $entry -CIMode $CIMode
            if ($check.Valid) {
                $suppressedCount++
                Write-Host "  [~] Suppressed: $($v.File):$($v.Line) - @$($v.Ref) (allowlisted)" -ForegroundColor DarkGray
            } else {
                $failedAllowlist += [PSCustomObject]@{ Violation = $v; Entry = $entry; Problems = $check.Problems }
            }
        } else {
            $newViolations += $v
        }
    }

    Write-Host ""

    if ($newViolations.Count -gt 0) {
        Write-Host "  Found $($newViolations.Count) unpinned reference(s):" -ForegroundColor Red
        foreach ($v in $newViolations) {
            Write-Host "    [X] $($v.File):$($v.Line) - @$($v.Ref)" -ForegroundColor Red
            Write-Host "        $($v.UsesLine)" -ForegroundColor Yellow
            Write-Host "        FIX: Replace @$($v.Ref) with @<full-40-char-SHA>" -ForegroundColor Green
        }
    }

    if ($failedAllowlist.Count -gt 0) {
        Write-Host "  Found $($failedAllowlist.Count) invalid allowlist entry(ies):" -ForegroundColor Magenta
        foreach ($f in $failedAllowlist) {
            $label = if ($CIMode) { "[X]" } else { "[!]" }
            $color = if ($CIMode) { "Red" } else { "Yellow" }
            Write-Host "    $label $($f.Violation.File):$($f.Violation.Line) - @$($f.Violation.Ref)" -ForegroundColor $color
            foreach ($p in $f.Problems) {
                Write-Host "        Allowlist problem: $p" -ForegroundColor $color
            }
        }
    }

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Summary: Violations=$($violations.Count), New=$($newViolations.Count), Suppressed=$suppressedCount, InvalidAllowlist=$($failedAllowlist.Count)" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    $exitCode = 0
    if ($newViolations.Count -gt 0) {
        Write-Host "FAILED: $($newViolations.Count) unpinned reference(s)" -ForegroundColor Red
        $exitCode = 1
    }
    if ($failedAllowlist.Count -gt 0 -and $CIMode) {
        Write-Host "FAILED: $($failedAllowlist.Count) invalid allowlist entry(ies) in CI mode" -ForegroundColor Red
        $exitCode = 1
    }
    if ($exitCode -eq 0) { Write-Host "PASSED" -ForegroundColor Green }

    return $exitCode
}

# ─── Self-Test ───────────────────────────────────────────────────────────────

function Invoke-SelfTest {
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Self-Test: lint-workflow-sha-pins.ps1" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""

    $fixtureDir = Join-Path $PSScriptRoot 'tests' 'fixtures'
    $passed = 0
    $failed = 0

    # Helper: create temp repo structure with a workflow file
    function New-TempRepoWithWorkflow {
        param([string]$FixtureFile)
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "sha-pin-test-$(Get-Random)"
        $wfDir = Join-Path $tmp '.github' 'workflows'
        New-Item -ItemType Directory -Path $wfDir -Force | Out-Null
        Copy-Item $FixtureFile (Join-Path $wfDir (Split-Path $FixtureFile -Leaf))
        return $tmp
    }

    function Remove-TempRepo {
        param([string]$TmpPath)
        if ($TmpPath -and (Test-Path $TmpPath)) {
            Remove-Item $TmpPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # ── Test 1: SHA-pinned fixtures should PASS ──
    Write-Host "Test 1: SHA-pinned fixtures → expect PASS" -ForegroundColor Cyan
    $pinnedFixture = Join-Path $fixtureDir 'sha-pinned.yml'
    if (-not (Test-Path $pinnedFixture)) {
        Write-Host "  SKIP: fixture not found: $pinnedFixture" -ForegroundColor Yellow
    } else {
        $tmp = New-TempRepoWithWorkflow $pinnedFixture
        try {
            $result = Invoke-Lint -RepoRoot $tmp -AllowlistFile '' -CIMode $true
            if ($result -eq 0) {
                Write-Host "  PASS" -ForegroundColor Green; $passed++
            } else {
                Write-Host "  FAIL (expected 0, got $result)" -ForegroundColor Red; $failed++
            }
        } finally { Remove-TempRepo $tmp }
    }

    # ── Test 2: Not-SHA-pinned fixtures should FAIL ──
    Write-Host "Test 2: Not-SHA-pinned fixtures → expect FAIL" -ForegroundColor Cyan
    $unpinnedFixture = Join-Path $fixtureDir 'not-sha-pinned.yml'
    if (-not (Test-Path $unpinnedFixture)) {
        Write-Host "  SKIP: fixture not found: $unpinnedFixture" -ForegroundColor Yellow
    } else {
        $tmp = New-TempRepoWithWorkflow $unpinnedFixture
        try {
            $result = Invoke-Lint -RepoRoot $tmp -AllowlistFile '' -CIMode $true
            if ($result -ne 0) {
                Write-Host "  PASS (correctly detected violations)" -ForegroundColor Green; $passed++
            } else {
                Write-Host "  FAIL (expected non-zero, got 0)" -ForegroundColor Red; $failed++
            }
        } finally { Remove-TempRepo $tmp }
    }

    # ── Test 3: Allowlist suppression ──
    Write-Host "Test 3: Allowlist suppression → expect PASS" -ForegroundColor Cyan
    if (-not (Test-Path $unpinnedFixture)) {
        Write-Host "  SKIP: fixture not found" -ForegroundColor Yellow
    } else {
        $tmp = New-TempRepoWithWorkflow $unpinnedFixture
        try {
            # Create allowlist matching the fixture violations
            $allowlistContent = @{
                entries = @(
                    @{
                        file = 'not-sha-pinned.yml'
                        repoPath = 'RicherTunes/Lidarr.Plugin.Common/.github/workflows/multi-plugin-smoke-test.yml'
                        owner = 'test'
                        expiresOn = ([DateTime]::UtcNow.AddDays(30)).ToString('yyyy-MM-dd')
                        reason = 'Self-test fixture'
                    }
                    @{
                        file = 'not-sha-pinned.yml'
                        repoPath = 'RicherTunes/Lidarr.Plugin.Common/.github/workflows/packaging-gates.yml'
                        owner = 'test'
                        expiresOn = ([DateTime]::UtcNow.AddDays(30)).ToString('yyyy-MM-dd')
                        reason = 'Self-test fixture'
                    }
                    @{
                        file = 'not-sha-pinned.yml'
                        repoPath = 'RicherTunes/Lidarr.Plugin.Common/.github/actions/init-common/action.yml'
                        owner = 'test'
                        expiresOn = ([DateTime]::UtcNow.AddDays(30)).ToString('yyyy-MM-dd')
                        reason = 'Self-test fixture'
                    }
                )
            } | ConvertTo-Json -Depth 5

            $allowlistFile = Join-Path $tmp 'allowlist.json'
            $allowlistContent | Out-File -FilePath $allowlistFile -Encoding utf8

            $result = Invoke-Lint -RepoRoot $tmp -AllowlistFile $allowlistFile -CIMode $true
            if ($result -eq 0) {
                Write-Host "  PASS" -ForegroundColor Green; $passed++
            } else {
                Write-Host "  FAIL (expected 0, got $result)" -ForegroundColor Red; $failed++
            }
        } finally { Remove-TempRepo $tmp }
    }

    # ── Test 4: Expired allowlist in CI mode should FAIL ──
    Write-Host "Test 4: Expired allowlist in CI mode → expect FAIL" -ForegroundColor Cyan
    if (-not (Test-Path $unpinnedFixture)) {
        Write-Host "  SKIP: fixture not found" -ForegroundColor Yellow
    } else {
        $tmp = New-TempRepoWithWorkflow $unpinnedFixture
        try {
            $allowlistContent = @{
                entries = @(
                    @{
                        file = 'not-sha-pinned.yml'
                        repoPath = 'RicherTunes/Lidarr.Plugin.Common/.github/workflows/multi-plugin-smoke-test.yml'
                        owner = 'test'
                        expiresOn = '2020-01-01'
                        reason = 'Expired entry for self-test'
                    }
                    @{
                        file = 'not-sha-pinned.yml'
                        repoPath = 'RicherTunes/Lidarr.Plugin.Common/.github/workflows/packaging-gates.yml'
                        owner = 'test'
                        expiresOn = '2020-01-01'
                        reason = 'Expired entry for self-test'
                    }
                    @{
                        file = 'not-sha-pinned.yml'
                        repoPath = 'RicherTunes/Lidarr.Plugin.Common/.github/actions/init-common/action.yml'
                        owner = 'test'
                        expiresOn = '2020-01-01'
                        reason = 'Expired entry for self-test'
                    }
                )
            } | ConvertTo-Json -Depth 5

            $allowlistFile = Join-Path $tmp 'allowlist.json'
            $allowlistContent | Out-File -FilePath $allowlistFile -Encoding utf8

            $result = Invoke-Lint -RepoRoot $tmp -AllowlistFile $allowlistFile -CIMode $true
            if ($result -ne 0) {
                Write-Host "  PASS (correctly failed on expired allowlist)" -ForegroundColor Green; $passed++
            } else {
                Write-Host "  FAIL (expected non-zero, got 0)" -ForegroundColor Red; $failed++
            }
        } finally { Remove-TempRepo $tmp }
    }

    # ── Test 5: Missing owner in CI mode should FAIL ──
    Write-Host "Test 5: Missing owner in CI mode → expect FAIL" -ForegroundColor Cyan
    if (-not (Test-Path $unpinnedFixture)) {
        Write-Host "  SKIP: fixture not found" -ForegroundColor Yellow
    } else {
        $tmp = New-TempRepoWithWorkflow $unpinnedFixture
        try {
            $allowlistContent = @{
                entries = @(
                    @{
                        file = 'not-sha-pinned.yml'
                        repoPath = 'RicherTunes/Lidarr.Plugin.Common/.github/workflows/multi-plugin-smoke-test.yml'
                        expiresOn = ([DateTime]::UtcNow.AddDays(30)).ToString('yyyy-MM-dd')
                        reason = 'Missing owner for self-test'
                    }
                    @{
                        file = 'not-sha-pinned.yml'
                        repoPath = 'RicherTunes/Lidarr.Plugin.Common/.github/workflows/packaging-gates.yml'
                        expiresOn = ([DateTime]::UtcNow.AddDays(30)).ToString('yyyy-MM-dd')
                        reason = 'Missing owner for self-test'
                    }
                    @{
                        file = 'not-sha-pinned.yml'
                        repoPath = 'RicherTunes/Lidarr.Plugin.Common/.github/actions/init-common/action.yml'
                        expiresOn = ([DateTime]::UtcNow.AddDays(30)).ToString('yyyy-MM-dd')
                        reason = 'Missing owner for self-test'
                    }
                )
            } | ConvertTo-Json -Depth 5

            $allowlistFile = Join-Path $tmp 'allowlist.json'
            $allowlistContent | Out-File -FilePath $allowlistFile -Encoding utf8

            $result = Invoke-Lint -RepoRoot $tmp -AllowlistFile $allowlistFile -CIMode $true
            if ($result -ne 0) {
                Write-Host "  PASS (correctly failed on missing owner)" -ForegroundColor Green; $passed++
            } else {
                Write-Host "  FAIL (expected non-zero, got 0)" -ForegroundColor Red; $failed++
            }
        } finally { Remove-TempRepo $tmp }
    }

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Self-Test Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'Green' })
    Write-Host "========================================" -ForegroundColor Cyan

    return $(if ($failed -gt 0) { 1 } else { 0 })
}

# ─── Entry Point ─────────────────────────────────────────────────────────────

if ($SelfTest) {
    $code = Invoke-SelfTest
    exit $code
}

$resolvedPath = Resolve-Path $Path -ErrorAction Stop
if (-not $AllowlistPath) {
    $AllowlistPath = Join-Path $resolvedPath '.github' 'sha-pin-allowlist.json'
}

$code = Invoke-Lint -RepoRoot $resolvedPath -AllowlistFile $AllowlistPath -CIMode $script:IsCIMode
exit $code
