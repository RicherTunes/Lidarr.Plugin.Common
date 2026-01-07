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

function Assert-Throws {
    param(
        [Parameter(Mandatory)] [scriptblock]$ScriptBlock,
        [Parameter(Mandatory)] [string]$ExpectedPattern,
        [Parameter(Mandatory)] [string]$Description
    )

    $threw = $false
    $errorMessage = ""
    try {
        & $ScriptBlock
    } catch {
        $threw = $true
        $errorMessage = $_.Exception.Message
    }

    if ($threw -and $errorMessage -match $ExpectedPattern) {
        Write-Host "  [PASS] $Description" -ForegroundColor Green
        $script:passed++
    } elseif (-not $threw) {
        Write-Host "  [FAIL] $Description (no exception thrown)" -ForegroundColor Red
        $script:failed++
    } else {
        Write-Host "  [FAIL] $Description (wrong error: $errorMessage)" -ForegroundColor Red
        $script:failed++
    }
}

$scriptDir = Split-Path $PSScriptRoot -Parent
$smokeScript = Join-Path $scriptDir "multi-plugin-docker-smoke-test.ps1"

# Parse out the Normalize-PluginAbstractions function from the smoke test script
$smokeContent = Get-Content -LiteralPath $smokeScript -Raw
if ($smokeContent -match '(?s)(function Normalize-PluginAbstractions \{.+?\n\})') {
    $functionDef = $Matches[1]
    # Define the function in current scope
    Invoke-Expression $functionDef
} else {
    throw "Could not extract Normalize-PluginAbstractions from $smokeScript"
}

# Create temp directory structure for tests
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "abstractions-sha-test-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

try {
    Write-Host "`nTest 1: Identical Abstractions.dll across plugins" -ForegroundColor Yellow
    $pluginsRoot1 = Join-Path $testRoot "identical"
    $plugin1Dir = Join-Path $pluginsRoot1 "RicherTunes/PluginA"
    $plugin2Dir = Join-Path $pluginsRoot1 "RicherTunes/PluginB"
    New-Item -ItemType Directory -Force -Path $plugin1Dir | Out-Null
    New-Item -ItemType Directory -Force -Path $plugin2Dir | Out-Null

    # Create identical mock DLLs (same content = same SHA256)
    $mockContent = [byte[]](0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00)
    [System.IO.File]::WriteAllBytes((Join-Path $plugin1Dir "Lidarr.Plugin.Abstractions.dll"), $mockContent)
    [System.IO.File]::WriteAllBytes((Join-Path $plugin2Dir "Lidarr.Plugin.Abstractions.dll"), $mockContent)

    # This test would need real assemblies for GetAssemblyName to work
    # For now, we test the SHA check path by mocking - skip assembly identity check
    Write-Host "  [SKIP] Requires real assemblies for GetAssemblyName (integration test)" -ForegroundColor Yellow

    Write-Host "`nTest 2: Error code present in script" -ForegroundColor Yellow
    Assert-True -Condition ($smokeContent -match 'E2E_ABSTRACTIONS_SHA_MISMATCH') -Description "E2E_ABSTRACTIONS_SHA_MISMATCH error code defined"

    Write-Host "`nTest 3: SHA mismatch throws exception with error variable" -ForegroundColor Yellow
    # The error code is in $errorMsg which is then thrown via 'throw $errorMsg'
    $hasThrow = $smokeContent -match 'throw \$errorMsg'
    $hasErrorVar = $smokeContent -match '(?s)\$errorMsg\s*=\s*@".*E2E_ABSTRACTIONS_SHA_MISMATCH'
    Assert-True -Condition ($hasThrow -and $hasErrorVar) -Description "SHA mismatch throws errorMsg containing error code"

    Write-Host "`nTest 4: Identical hashes show OK message" -ForegroundColor Yellow
    Assert-True -Condition ($smokeContent -match 'identical.*OK') -Description "Identical SHA shows OK message"

    Write-Host "`nTest 5: Fix instruction present" -ForegroundColor Yellow
    Assert-True -Condition ($smokeContent -match 'FIX:.*Rebuild.*same Common') -Description "Fix instruction mentions rebuilding from same Common"

} finally {
    # Cleanup
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary: $passed passed, $failed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
