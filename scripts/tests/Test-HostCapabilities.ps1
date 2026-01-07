#!/usr/bin/env pwsh
# Tests for e2e-host-capabilities.psm1 module

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Host Capabilities Module Tests" -ForegroundColor Cyan
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
$modulePath = Join-Path $scriptDir "lib/e2e-host-capabilities.psm1"

# Test 1: Module exists
Write-Host "`nTest 1: Module exists" -ForegroundColor Yellow
Assert-True -Condition (Test-Path -LiteralPath $modulePath) -Description "e2e-host-capabilities.psm1 exists"

# Import module
Import-Module $modulePath -Force

# Test 2: Functions exported
Write-Host "`nTest 2: Functions exported" -ForegroundColor Yellow
$exportedFunctions = (Get-Module -Name 'e2e-host-capabilities').ExportedFunctions.Keys
Assert-True -Condition ($exportedFunctions -contains 'Test-HostALCFix') -Description "Test-HostALCFix is exported"
Assert-True -Condition ($exportedFunctions -contains 'Get-HostCapabilities') -Description "Get-HostCapabilities is exported"

# Test 3: Module content checks
Write-Host "`nTest 3: Module content checks" -ForegroundColor Yellow
$moduleContent = Get-Content -LiteralPath $modulePath -Raw
Assert-True -Condition ($moduleContent -match 'PluginContexts') -Description "Module references PluginContexts field"
Assert-True -Condition ($moduleContent -match 'PR.*5662') -Description "Module references upstream PR #5662"
Assert-True -Condition ($moduleContent -match 'strings-probe') -Description "Module uses strings-based detection"

# Test 4: Function signature checks (non-container tests)
Write-Host "`nTest 4: Function returns correct structure" -ForegroundColor Yellow
# Test with non-existent container (should return probe-failed or similar)
$result = $null
try {
    $result = Test-HostALCFix -ContainerName "nonexistent-container-12345"
} catch {
    # Expected - container doesn't exist
}

if ($result) {
    Assert-True -Condition ($null -ne $result.HasFix) -Description "Test-HostALCFix returns HasFix property"
    Assert-True -Condition ($null -ne $result.Method) -Description "Test-HostALCFix returns Method property"
    Assert-True -Condition ($null -ne $result.Details) -Description "Test-HostALCFix returns Details property"
} else {
    Write-Host "  [SKIP] Test-HostALCFix requires Docker container (non-blocking)" -ForegroundColor Yellow
}

# Test 5: Get-HostCapabilities structure
Write-Host "`nTest 5: Get-HostCapabilities structure" -ForegroundColor Yellow
$caps = $null
try {
    $caps = Get-HostCapabilities -ContainerName "nonexistent-container-12345"
} catch {
    # Expected - container doesn't exist
}

if ($caps) {
    Assert-True -Condition ($null -ne $caps.alcFix) -Description "Get-HostCapabilities returns alcFix section"
    Assert-True -Condition ($null -ne $caps.probeTimestamp) -Description "Get-HostCapabilities returns probeTimestamp"
    Assert-True -Condition ($caps.alcFix.prUrl -match '5662') -Description "alcFix includes PR URL"
} else {
    Write-Host "  [SKIP] Get-HostCapabilities requires Docker container (non-blocking)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Summary: $passed passed, $failed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
