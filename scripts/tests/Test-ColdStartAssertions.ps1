#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tests for scripts/ci/assert-cold-start.ps1
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$assertScript = Join-Path $repoRoot 'scripts/ci/assert-cold-start.ps1'
$fixturesDir = Join-Path $PSScriptRoot 'fixtures/cold-start'

if (-not (Test-Path $assertScript)) {
    throw "assert-cold-start.ps1 not found: $assertScript"
}

$passed = 0
$failed = 0

function Invoke-Assert {
    param(
        [string]$Name,
        [string]$Fixture,
        [string]$Plugins,
        [string]$HasQobuzCreds,
        [string]$HasTidalCreds,
        [string]$HasBrainarrCreds,
        [int]$ExpectedExitCode
    )

    Write-Host "Testing: $Name" -ForegroundColor Cyan

    $fixturePath = Join-Path $fixturesDir $Fixture
    if (-not (Test-Path $fixturePath)) {
        throw "Fixture not found: $fixturePath"
    }

    $output = @()
    $exitCode = 0
    try {
        $cmd = "& '$assertScript' -ManifestPath '$fixturePath' -Plugins '$Plugins' -HasQobuzCreds '$HasQobuzCreds' -HasTidalCreds '$HasTidalCreds' -HasBrainarrCreds '$HasBrainarrCreds' 2>&1; exit `$LASTEXITCODE"
        $output = & pwsh -NoProfile -Command $cmd
        $exitCode = $LASTEXITCODE
    } catch {
        $output = @($_.Exception.Message)
        $exitCode = 1
    }

    if ($exitCode -eq $ExpectedExitCode) {
        Write-Host "  PASS" -ForegroundColor Green
        $script:passed++
        return
    }

    Write-Host "  FAIL: Expected exit $ExpectedExitCode, got $exitCode" -ForegroundColor Red
    foreach ($line in $output | Select-Object -First 5) {
        Write-Host "    $line" -ForegroundColor Yellow
    }
    $script:failed++
}

Write-Host "========================================" -ForegroundColor White
Write-Host "Cold-Start Assertion Tests" -ForegroundColor White
Write-Host "========================================" -ForegroundColor White
Write-Host ""

Invoke-Assert -Name "No secrets: Configure skipped, no persistence attempts" `
    -Fixture "no-secrets.json" `
    -Plugins "Qobuzarr,Tidalarr,Brainarr" `
    -HasQobuzCreds "false" -HasTidalCreds "false" -HasBrainarrCreds "false" `
    -ExpectedExitCode 0

Invoke-Assert -Name "With secrets: Configure success, persistence written" `
    -Fixture "with-secrets.json" `
    -Plugins "Qobuzarr,Tidalarr" `
    -HasQobuzCreds "true" -HasTidalCreds "true" -HasBrainarrCreds "false" `
    -ExpectedExitCode 0

Invoke-Assert -Name "Mismatch: claims secrets but Configure skipped -> fail" `
    -Fixture "no-secrets.json" `
    -Plugins "Qobuzarr,Tidalarr" `
    -HasQobuzCreds "true" -HasTidalCreds "true" -HasBrainarrCreds "false" `
    -ExpectedExitCode 1

Write-Host ""
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor Red

if ($failed -gt 0) {
    exit 1
}

