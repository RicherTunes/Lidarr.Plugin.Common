#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Guards Common docs/scripts against stale plugin-root GitHub workflow guidance.

.DESCRIPTION
    Plugin repos are Gitea-primary and the ecosystem manifest currently enforces
    exactly one guarded GitHub CI mirror per plugin. This test catches Common docs
    or helper scripts that describe either the old zero-workflow policy or
    unguarded duplicate GitHub workflow maintenance.
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
    -Name 'Promotion checklist requires guarded plugin GitHub mirrors' `
    -Condition ($promotion -match 'exactly one guarded GitHub CI mirror') `
    -Details 'The checklist should state the current one-guarded-mirror contract.'
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
    -Name 'CI SHA pins doc documents guarded plugin GitHub mirrors' `
    -Condition ($pinInventory -match 'exactly one guarded GitHub CI mirror') `
    -Details 'The active plugin repos now enforce one guarded plugin-root CI mirror.'
Assert-True `
    -Name 'CI SHA pins doc points plugin mirror policy at the ecosystem contract' `
    -Condition ($pinInventory -match 'verify-ecosystem-ci-contract\.ps1') `
    -Details 'Plugin mirror drift should be governed by Common contract checks.'

$reusable = Read-RepoText 'docs/CI_REUSABLE_WORKFLOWS.md'
Assert-True `
    -Name 'Reusable workflow proposals are marked historical for plugin CI' `
    -Condition ($reusable -match 'HISTORICAL.*Gitea-primary plugin CI') `
    -Details 'The old reusable-workflow proposal must not read as the current mirror implementation.'

$ciGates = Read-RepoText 'docs/dev-guide/CI_GATES.md'
Assert-True `
    -Name 'CI gates guide documents the one guarded mirror policy' `
    -Condition ($ciGates -match 'exactly one guarded GitHub CI mirror' -and $ciGates -match 'verify-ecosystem-ci-contract\.ps1') `
    -Details 'Plugin CI guidance must point authors at the current fail-by-default mirror contract.'
Assert-True `
    -Name 'CI gates guide does not recommend extra plugin GitHub workflows' `
    -Condition ($ciGates -notmatch 'recommended filename: `?\.github/workflows/packaging-gates\.yml' -and $ciGates -notmatch 'uses:\s+RicherTunes/lidarr\.plugin\.common/\.github/workflows/packaging-gates\.yml@main') `
    -Details 'Extra plugin-root GitHub workflows would violate the one-mirror ecosystem contract.'

$bulkScript = Read-RepoText 'scripts/bulk-update-workflow-pins.sh'
Assert-True `
    -Name 'Bulk workflow pin script is explicitly deprecated' `
    -Condition ($bulkScript -match 'DEPRECATED.*bulk plugin workflow pin mutation') `
    -Details 'A stale mutator should fail closed instead of silently changing plugin repos.'
Assert-True `
    -Name 'Bulk workflow pin script does not mutate plugin .github/workflows files' `
    -Condition ($bulkScript -notmatch 'git add \.github/workflows|perl -pi|PLUGINS=\(') `
    -Details 'Plugin mirror changes should be reviewed per repo and guarded by the ecosystem contract.'

Write-Host ''
if ($fail -gt 0) {
    Write-Host "$fail plugin CI truth doc test(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All plugin CI truth docs tests passed.' -ForegroundColor Green
