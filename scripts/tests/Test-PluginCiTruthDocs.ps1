#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Guards Common docs/scripts against reintroducing plugin-root GitHub workflow guidance.

.DESCRIPTION
    Plugin repos are Gitea-primary and the ecosystem manifest currently enforces
    zero plugin-root .github/workflows files. This test catches stale Common docs
    or helper scripts that tell maintainers to recreate those duplicate workflows.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$fail = 0

function Assert-True {
    param(
        [string]$Name,
        [bool]$Condition,
        [string]$Details = ''
    )

    if ($Condition) {
        Write-Host "[PASS] $Name" -ForegroundColor Green
        return
    }

    Write-Host "[FAIL] $Name" -ForegroundColor Red
    if ($Details) {
        Write-Host "       $Details" -ForegroundColor DarkGray
    }
    $script:fail++
}

function Read-RepoText {
    param([string]$RelativePath)
    return Get-Content -LiteralPath (Join-Path $RepoRoot $RelativePath) -Raw
}

$promotion = Read-RepoText 'docs/ECOSYSTEM_PROMOTION_CHECKLIST.md'
Assert-True `
    -Name 'Promotion checklist forbids plugin-root GitHub workflows' `
    -Condition ($promotion -match 'must not carry plugin-root GitHub Actions workflows') `
    -Details 'The checklist should state the current zero-workflow contract.'
Assert-True `
    -Name 'Promotion checklist no longer requires plugin .github submodule-pin workflow' `
    -Condition ($promotion -notmatch '\.github/workflows/submodule-pin\.ya?ml') `
    -Details 'Plugin repos are Gitea-primary; submodule pin checks live in .gitea/workflows/ci.yml.'
Assert-True `
    -Name 'Promotion checklist includes all five plugins' `
    -Condition ($promotion -match 'AmazonMusicarr' -and $promotion -match 'Brainarr') `
    -Details 'Common promotion docs must cover amazonmusicarr, applemusicarr, brainarr, qobuzarr, and tidalarr.'

$pinInventory = Read-RepoText 'docs/CI_SHA_PINS.md'
Assert-True `
    -Name 'CI SHA pins doc does not list deleted plugin GitHub workflow paths as active work' `
    -Condition ($pinInventory -notmatch '(qobuzarr|tidalarr|applemusicarr|brainarr)/\.github/workflows') `
    -Details 'The active plugin repos now enforce zero plugin-root GitHub workflows.'
Assert-True `
    -Name 'CI SHA pins doc scopes active action-pin policy to Common workflows' `
    -Condition ($pinInventory -match 'active scope is `lidarr\.plugin\.common/\.github/workflows`') `
    -Details 'Plugin GitHub workflow pinning is no longer an active maintenance path.'

$reusable = Read-RepoText 'docs/CI_REUSABLE_WORKFLOWS.md'
Assert-True `
    -Name 'Reusable workflow proposals are marked superseded for plugin CI' `
    -Condition ($reusable -match 'SUPERSEDED.*Gitea-primary plugin CI') `
    -Details 'The old GitHub reusable-workflow proposal must not read as current guidance.'

$bulkScript = Read-RepoText 'scripts/bulk-update-workflow-pins.sh'
Assert-True `
    -Name 'Bulk workflow pin script is explicitly deprecated' `
    -Condition ($bulkScript -match 'DEPRECATED.*plugin-root GitHub workflow mirrors') `
    -Details 'A stale mutator should fail closed instead of silently changing plugin repos.'
Assert-True `
    -Name 'Bulk workflow pin script no longer mutates plugin .github/workflows files' `
    -Condition ($bulkScript -notmatch 'git add \.github/workflows|perl -pi|PLUGINS=\(') `
    -Details 'Plugin workflow pinning was removed; do not keep write paths for deleted mirrors.'

Write-Host ''
if ($fail -gt 0) {
    Write-Host "$fail plugin CI truth doc test(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All plugin CI truth docs tests passed.' -ForegroundColor Green
