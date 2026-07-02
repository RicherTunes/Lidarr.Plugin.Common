#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails if tracked files contain unresolved git merge conflict markers.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$pattern = '^(<<<<<<< |=======$|>>>>>>> )'

$matches = @(& git -C $RepoRoot grep -n -E $pattern -- . 2>$null)
$exitCode = $LASTEXITCODE

if ($exitCode -eq 1) {
    Write-Host '[PASS] No unresolved merge conflict markers found.' -ForegroundColor Green
    exit 0
}

if ($exitCode -ne 0) {
    Write-Host "[FAIL] git grep failed with exit code $exitCode." -ForegroundColor Red
    exit $exitCode
}

Write-Host '[FAIL] Unresolved merge conflict markers found:' -ForegroundColor Red
$matches | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
exit 1
