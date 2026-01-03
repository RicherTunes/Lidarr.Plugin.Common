#!/usr/bin/env pwsh
# Validates that our ILRepack response file uses correct /internalize:<exclude_file> syntax.

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "ILRepack RSP Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$passed = 0
$failed = 0

function Assert-True {
    param(
        [Parameter(Mandatory)] [bool]$Condition,
        [Parameter(Mandatory)] [string]$Description
    )

    if ($Condition) {
        Write-Host "  [PASS] $Description" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  [FAIL] $Description" -ForegroundColor Red
        $script:failed++
    }
}

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$rspPath = Join-Path $repoRoot 'tools/ilrepack.rsp'

Assert-True -Condition (Test-Path -LiteralPath $rspPath) -Description "tools/ilrepack.rsp exists"

$content = Get-Content -LiteralPath $rspPath -Raw

Assert-True -Condition ($content -match '/internalize:\$\(InternalizeExclude\)') -Description "Uses /internalize:<exclude_file> syntax"
Assert-True -Condition (-not ($content -match '/internalize:@\$\(InternalizeExclude\)')) -Description "Does not use response-file (@) prefix for InternalizeExclude"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary: $passed passed, $failed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
