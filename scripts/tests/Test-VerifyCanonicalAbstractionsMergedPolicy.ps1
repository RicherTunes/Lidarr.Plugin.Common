#!/usr/bin/env pwsh
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot '../Verify-CanonicalAbstractions.ps1'
$failed = 0
$testRoot = $null

function Write-Pass([string]$Message) { Write-Host "  [PASS] $Message" -ForegroundColor Green }
function Write-Fail([string]$Message) { Write-Host "  [FAIL] $Message" -ForegroundColor Red; $script:failed++ }

try {
    Write-Host '===============================================' -ForegroundColor Cyan
    Write-Host 'Test-VerifyCanonicalAbstractionsMergedPolicy' -ForegroundColor Cyan
    Write-Host '===============================================' -ForegroundColor Cyan

    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "Verifier script not found at $scriptPath"
    }

    $testRoot = Join-Path ([IO.Path]::GetTempPath()) "verify-abstractions-merged-$(New-Guid)"
    $payload = Join-Path $testRoot 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $payload 'Lidarr.Plugin.Test.dll') -Value 'main' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $payload 'plugin.json') -Value '{}' -Encoding UTF8

    $zip = Join-Path $testRoot 'plugin.zip'
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip

    Write-Host "`n[TEST] merged package without Abstractions sidecar passes..." -ForegroundColor Cyan
    $output = & pwsh -NoProfile -File $scriptPath -PackagePaths @($zip) 2>&1
    $exit = $LASTEXITCODE

    if ($exit -eq 0 -and (($output -join "`n") -match 'No Abstractions sidecars|merged')) {
        Write-Pass 'merged package without Abstractions sidecar accepted'
    } else {
        Write-Fail "Expected verifier to accept merged package. Exit=$exit Output=$($output -join "`n")"
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
