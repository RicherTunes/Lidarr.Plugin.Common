#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validates xUnit test traits against the Common-owned CI trait policy.
#>

[CmdletBinding()]
param(
    [string]$Path = '.',
    [switch]$CI
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$modulePath = Join-Path $PSScriptRoot 'lib/test-trait-policy.psm1'
Import-Module $modulePath -Force

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host 'Test Trait Policy Linter' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''
Write-Host "Scanning: $Path" -ForegroundColor White
if ($CI) {
    Write-Host 'Mode: CI (strict)' -ForegroundColor Yellow
}

$result = Test-TestTraitPolicy -Path $Path

Write-Host ''
Write-Host "Files scanned: $($result.FilesScanned)" -ForegroundColor White
Write-Host "Traits found:  $($result.TraitUsages.Count)" -ForegroundColor White

if ($result.Success) {
    Write-Host ''
    Write-Host '[PASS] Test trait policy passed.' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host "[FAIL] Found $($result.Violations.Count) test trait policy violation(s):" -ForegroundColor Red
Write-Host ''

foreach ($violation in ($result.Violations | Sort-Object File, Line, Code)) {
    Write-Host "  $($violation.File):$($violation.Line)" -ForegroundColor Yellow
    Write-Host "    $($violation.Code): $($violation.Message)" -ForegroundColor Gray
}

Write-Host ''
Write-Host 'Deterministic CI filter:' -ForegroundColor Cyan
Write-Host "  $(Get-LocalCiDeterministicFilter)" -ForegroundColor DarkGray

if ($CI) {
    exit 1
}

exit 0
