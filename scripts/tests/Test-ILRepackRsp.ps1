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
$excludePath = Join-Path $repoRoot 'tools/internalize.exclude'

Assert-True -Condition (Test-Path -LiteralPath $rspPath) -Description "tools/ilrepack.rsp exists"
Assert-True -Condition (Test-Path -LiteralPath $excludePath) -Description "tools/internalize.exclude exists"

$content = Get-Content -LiteralPath $rspPath -Raw
$excludeContent = [string](Get-Content -LiteralPath $excludePath -Raw -ErrorAction SilentlyContinue)

Assert-True -Condition ($content -match '/internalize:\$\(InternalizeExclude\)') -Description "Uses /internalize:<exclude_file> syntax"
Assert-True -Condition (-not ($content -match '/internalize:@\$\(InternalizeExclude\)')) -Description "Does not use response-file (@) prefix for InternalizeExclude"
Assert-True -Condition ($excludeContent -notmatch 'Lidarr\.Plugin\.Abstractions') -Description "Does not exclude Abstractions from internalization"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary: $passed passed, $failed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
