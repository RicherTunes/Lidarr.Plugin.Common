#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run the shared lint gates used by plugin CI workflows.

.DESCRIPTION
    Centralizes plugin lint CI so Gitea and GitHub workflows do not drift.
    Each gate runs in a child PowerShell process because the underlying lint
    scripts and repo-local contract tests use explicit exit codes.
#>

[CmdletBinding()]
param(
    [string]$RepoPath = '.',
    [string]$CommonRoot = 'ext/Lidarr.Plugin.Common',
    [ValidateSet('interactive', 'ci')]
    [string]$Mode = 'ci',
    [switch]$SkipDateParsing,
    [switch]$SkipSyncOverAsync,
    [switch]$SkipTestTraits,
    [Alias('SkipVersionContract')]
    [switch]$SkipEcosystemParity,
    [switch]$SkipPluginContractTests,
    [switch]$SkipDocRefs,
    [switch]$SkipGiteaSecretScan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-RequiredPath {
    param(
        [string]$Path,
        [string]$Name
    )

    try {
        return (Resolve-Path -LiteralPath $Path).Path
    }
    catch {
        throw "$Name not found: $Path"
    }
}

function Resolve-CommonRootPath {
    param(
        [string]$Root,
        [string]$ResolvedRepoPath
    )

    $candidate = if ([System.IO.Path]::IsPathRooted($Root)) {
        $Root
    }
    else {
        Join-Path $ResolvedRepoPath $Root
    }

    return Resolve-RequiredPath -Path $candidate -Name 'CommonRoot'
}

function Invoke-LintGate {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [string[]]$Arguments
    )

    if (-not (Test-Path -LiteralPath $ScriptPath)) {
        throw "Required lint gate script not found for ${Name}: $ScriptPath"
    }

    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    Write-Host "    $ScriptPath" -ForegroundColor DarkGray

    if (Test-IsPesterScript -ScriptPath $ScriptPath) {
        $escapedPath = $ScriptPath.Replace("'", "''")
        & pwsh -NoProfile -Command "Import-Module Pester -ErrorAction Stop; Invoke-Pester -Path '$escapedPath' -CI"
    }
    else {
        & pwsh -NoProfile -File $ScriptPath @Arguments
    }

    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        throw "$Name gate failed with exit code $exitCode."
    }
}

function Test-IsPesterScript {
    param([string]$ScriptPath)

    $content = Get-Content -LiteralPath $ScriptPath -Raw
    return $content -match '(?m)^\s*#Requires\s+-Modules?\s+Pester\b' -or
        $content -match '(?m)^\s*Describe\s+'
}

function Invoke-PluginContractTests {
    param([string]$ResolvedRepoPath)

    $testRoot = Join-Path $ResolvedRepoPath 'scripts/tests'
    if (-not (Test-Path -LiteralPath $testRoot)) {
        Write-Host ""
        Write-Host "==> Plugin contract tests" -ForegroundColor Cyan
        Write-Host "    No scripts/tests directory found; skipping." -ForegroundColor DarkGray
        return
    }

    $tests = @(Get-ChildItem -LiteralPath $testRoot -File -Filter '*.ps1' | Sort-Object Name)
    if ($tests.Count -eq 0) {
        Write-Host ""
        Write-Host "==> Plugin contract tests" -ForegroundColor Cyan
        Write-Host "    No PowerShell tests found under scripts/tests; skipping." -ForegroundColor DarkGray
        return
    }

    foreach ($test in $tests) {
        Invoke-LintGate `
            -Name "Plugin contract test: $($test.Name)" `
            -ScriptPath $test.FullName `
            -Arguments @()
    }
}

try {
    $resolvedRepoPath = Resolve-RequiredPath -Path $RepoPath -Name 'RepoPath'
    $resolvedCommonRoot = Resolve-CommonRootPath -Root $CommonRoot -ResolvedRepoPath $resolvedRepoPath

    Write-Host "Shared plugin lint gates" -ForegroundColor Cyan
    Write-Host "RepoPath:   $resolvedRepoPath" -ForegroundColor White
    Write-Host "CommonRoot: $resolvedCommonRoot" -ForegroundColor White
    Write-Host "Mode:       $Mode" -ForegroundColor White

    if (-not $SkipDateParsing) {
        Invoke-LintGate `
            -Name 'Date parsing' `
            -ScriptPath (Join-Path $resolvedCommonRoot 'scripts/lint-date-parsing.ps1') `
            -Arguments @('-Path', $resolvedRepoPath, '-Mode', $Mode)
    }

    if (-not $SkipSyncOverAsync) {
        Invoke-LintGate `
            -Name 'Sync-over-async' `
            -ScriptPath (Join-Path $resolvedCommonRoot 'scripts/lint-sync-over-async.ps1') `
            -Arguments @('-Path', $resolvedRepoPath, '-Mode', $Mode)
    }

    if (-not $SkipTestTraits) {
        Invoke-LintGate `
            -Name 'Test trait policy' `
            -ScriptPath (Join-Path $resolvedCommonRoot 'scripts/lint-test-traits.ps1') `
            -Arguments @('-Path', $resolvedRepoPath, '-CI')
    }

    if (-not $SkipEcosystemParity) {
        Invoke-LintGate `
            -Name 'Ecosystem parity' `
            -ScriptPath (Join-Path $resolvedCommonRoot 'scripts/ecosystem-parity-lint.ps1') `
            -Arguments @('-RepoPath', $resolvedRepoPath, '-CommonRoot', $resolvedCommonRoot, '-Check', 'all', '-Mode', $Mode)
    }

    if (-not $SkipDocRefs) {
        Invoke-LintGate `
            -Name 'Doc refs' `
            -ScriptPath (Join-Path $resolvedCommonRoot 'scripts/lint-doc-script-refs.ps1') `
            -Arguments @('-RepoRoot', $resolvedRepoPath, '-CI')
    }

    if (-not $SkipGiteaSecretScan) {
        Invoke-LintGate `
            -Name 'Gitea secret-scan workflow' `
            -ScriptPath (Join-Path $resolvedCommonRoot 'scripts/lint-gitea-secret-scan.ps1') `
            -Arguments @('-RepoPath', $resolvedRepoPath, '-CI')
    }

    if (-not $SkipPluginContractTests) {
        Invoke-PluginContractTests -ResolvedRepoPath $resolvedRepoPath
    }

    Write-Host ""
    Write-Host "[OK] Shared plugin lint gates passed." -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ""
    Write-Error $_.Exception.Message
    exit 1
}
