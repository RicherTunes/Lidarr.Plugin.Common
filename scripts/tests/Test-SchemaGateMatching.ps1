#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Hermetic contract test for Schema gate matching logic.
.DESCRIPTION
    The Schema gate must be able to detect plugins whose schema items expose either:
      - implementationName (human-friendly), or
      - implementation (type/implementation identifier)

    Brainarr currently uses implementationName like "Brainarr AI Music Discovery"
    but implementation is "Brainarr". The Schema gate must match on either field
    with exact (case-insensitive) equality, without fuzzy matching.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Schema Gate Matching Contract" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$passed = 0
$failed = 0

function Assert-True {
    param([string]$Name, [bool]$Condition)
    if ($Condition) {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
        return $true
    }
    Write-Host "  [FAIL] $Name" -ForegroundColor Red
    $script:failed++
    return $false
}

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$gatesPath = Join-Path $repoRoot 'scripts/lib/e2e-gates.psm1'
if (-not (Test-Path $gatesPath)) { throw "Module not found: $gatesPath" }

$content = Get-Content $gatesPath -Raw -ErrorAction Stop

$start = $content.IndexOf('function Test-SchemaItemMatchesPlugin')
$end = $content.IndexOf('function Get-HostEnablePluginsFromContainer')
if ($start -lt 0 -or $end -lt 0 -or $end -le $start) {
    throw "Failed to locate Test-SchemaItemMatchesPlugin block in $gatesPath"
}

$block = $content.Substring($start, $end - $start)

Write-Host ""
Write-Host "Contract: matcher reads both implementationName and implementation" -ForegroundColor Cyan
Assert-True "Reads implementationName" ($block -match "implementationName")
Assert-True "Reads implementation" ($block.Contains("['implementation']") -or $block.Contains(".implementation"))

Write-Host ""
Write-Host "Contract: no early return on empty implementationName alone" -ForegroundColor Cyan
Assert-True "No early return on implementationName whitespace" ($block -notmatch 'IsNullOrWhiteSpace\(\[string\]\$implementationName\)\)\s*\{\s*return\s+\$false\s*\}')

Write-Host ""
Write-Host "Contract: exact, case-insensitive equality only (no fuzzy matching)" -ForegroundColor Cyan
Assert-True "Uses OrdinalIgnoreCase equality" ($block -match "OrdinalIgnoreCase")
Assert-True "Does not use -like" ($block -notmatch "\\s-like\\s")
Assert-True "Does not use Contains(" (-not $block.Contains('.Contains('))

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ("Summary: {0} passed, {1} failed" -f $passed, $failed) -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
