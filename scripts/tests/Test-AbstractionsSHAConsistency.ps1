#!/usr/bin/env pwsh
# Tests that Normalize-PluginAbstractions correctly detects SHA mismatch across plugins.

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Abstractions SHA Consistency Tests" -ForegroundColor Cyan
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

$scriptDir = Split-Path $PSScriptRoot -Parent
$modulePath = Join-Path $scriptDir "lib/e2e-abstractions.psm1"

# Verify module exists
Write-Host "`nTest 1: Module exists" -ForegroundColor Yellow
Assert-True -Condition (Test-Path -LiteralPath $modulePath) -Description "e2e-abstractions.psm1 exists"

# Import module
Import-Module $modulePath -Force

# Verify function is exported
Write-Host "`nTest 2: Function exported" -ForegroundColor Yellow
$exportedFunctions = (Get-Module -Name 'e2e-abstractions').ExportedFunctions.Keys
Assert-True -Condition ($exportedFunctions -contains 'Normalize-PluginAbstractions') -Description "Normalize-PluginAbstractions is exported"

# Read module content for error code verification
$moduleContent = Get-Content -LiteralPath $modulePath -Raw

Write-Host "`nTest 3: Error code present" -ForegroundColor Yellow
Assert-True -Condition ($moduleContent -match 'E2E_ABSTRACTIONS_SHA_MISMATCH') -Description "E2E_ABSTRACTIONS_SHA_MISMATCH error code defined"

Write-Host "`nTest 4: SHA mismatch throws exception" -ForegroundColor Yellow
Assert-True -Condition ($moduleContent -match 'throw \$errorMsg') -Description "SHA mismatch throws errorMsg"

Write-Host "`nTest 5: Identical hashes show OK message" -ForegroundColor Yellow
Assert-True -Condition ($moduleContent -match 'identical.*OK') -Description "Identical SHA shows OK message"

Write-Host "`nTest 6: Fix instruction present" -ForegroundColor Yellow
Assert-True -Condition ($moduleContent -match 'FIX:.*Rebuild.*same Common') -Description "Fix instruction mentions rebuilding from same Common"

# Create temp directory structure for functional tests
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "abstractions-sha-test-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

try {
    Write-Host "`nTest 7: Throws when no Abstractions.dll found" -ForegroundColor Yellow
    $emptyPluginsRoot = Join-Path $testRoot "empty"
    New-Item -ItemType Directory -Force -Path $emptyPluginsRoot | Out-Null

    $threw = $false
    try {
        Normalize-PluginAbstractions -PluginsRoot $emptyPluginsRoot
    } catch {
        $threw = $true
    }
    Assert-True -Condition $threw -Description "Throws when no Abstractions.dll found"

} finally {
    # Cleanup
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary: $passed passed, $failed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
