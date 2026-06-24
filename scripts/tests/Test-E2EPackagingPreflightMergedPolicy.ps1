#!/usr/bin/env pwsh
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$gatesModule = Join-Path $PSScriptRoot '../lib/e2e-gates.psm1'
$failed = 0
$testRoot = $null

function Write-Pass([string]$Message) { Write-Host "  [PASS] $Message" -ForegroundColor Green }
function Write-Fail([string]$Message) { Write-Host "  [FAIL] $Message" -ForegroundColor Red; $script:failed++ }

try {
    Write-Host '===============================================' -ForegroundColor Cyan
    Write-Host 'Test-E2EPackagingPreflightMergedPolicy' -ForegroundColor Cyan
    Write-Host '===============================================' -ForegroundColor Cyan

    if (-not (Test-Path -LiteralPath $gatesModule)) {
        throw "E2E gates module not found at $gatesModule"
    }

    Import-Module $gatesModule -Force

    $testRoot = Join-Path ([IO.Path]::GetTempPath()) "e2e-packaging-policy-$(New-Guid)"
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

    Set-Content -LiteralPath (Join-Path $testRoot 'Lidarr.Plugin.Test.dll') -Value 'main' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $testRoot 'Lidarr.Plugin.Abstractions.dll') -Value 'sidecar' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $testRoot 'Lidarr.Plugin.Common.dll') -Value 'sidecar' -Encoding UTF8

    Write-Host "`n[TEST] PackagingPreflight rejects merged sidecars..." -ForegroundColor Cyan
    $result = Test-PackagingPreflight -PluginPath $testRoot

    if ($result.Success) {
        Write-Fail 'Packaging preflight should fail when Abstractions/Common sidecars are present'
    } elseif ($result.ForbiddenDlls -contains 'Lidarr.Plugin.Abstractions.dll' -and $result.ForbiddenDlls -contains 'Lidarr.Plugin.Common.dll') {
        Write-Pass 'Abstractions/Common sidecars rejected'
    } else {
        Write-Fail "Expected Abstractions/Common in ForbiddenDlls. Actual: $($result.ForbiddenDlls -join ', ')"
    }
}
finally {
    if ($testRoot -and (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failed -gt 0) {
    Write-Host ""
    Write-Host "FAILED: $failed test(s)" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host 'All tests passed.' -ForegroundColor Green
