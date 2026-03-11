#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tests for validate-manifest.ps1

.DESCRIPTION
    Runs validation tests against fixture files to verify:
    - Valid manifests return exit 0
    - Invalid manifests return exit 1 with appropriate errors
    - Missing validators return exit 2 with install hints

.EXAMPLE
    ./scripts/tests/Test-ValidateManifest.ps1
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $PSScriptRoot
$validateScript = Join-Path $scriptRoot 'validate-manifest.ps1'
$fixturesDir = Join-Path $PSScriptRoot 'fixtures/manifest-validation'

$passed = 0
$failed = 0
$results = @()

function Test-Fixture {
    param(
        [string]$Name,
        [string]$FixturePath,
        [int]$ExpectedExitCode,
        [string]$ExpectedErrorPattern = $null,
        [string]$Validator = 'auto'
    )

    Write-Host "Testing: $Name" -ForegroundColor Cyan

    $output = @()
    $exitCode = 0

    try {
        # Run validator and capture output
        $output = & pwsh -NoProfile -Command "& '$validateScript' -ManifestPath '$FixturePath' -Validator '$Validator' -Quiet 2>&1; exit `$LASTEXITCODE"
        $exitCode = $LASTEXITCODE
    }
    catch {
        $output = @($_.Exception.Message)
        $exitCode = 1
    }

    $testPassed = $true
    $failureReason = $null

    # Check exit code
    if ($exitCode -ne $ExpectedExitCode) {
        $testPassed = $false
        $failureReason = "Expected exit code $ExpectedExitCode, got $exitCode"
    }

    # Check error pattern if specified
    if ($testPassed -and $ExpectedErrorPattern -and $ExpectedExitCode -ne 0) {
        $outputText = $output -join "`n"
        if ($outputText -notmatch $ExpectedErrorPattern) {
            $testPassed = $false
            $failureReason = "Expected error pattern '$ExpectedErrorPattern' not found in output"
        }
    }

    if ($testPassed) {
        Write-Host "  PASS" -ForegroundColor Green
        $script:passed++
    }
    else {
        Write-Host "  FAIL: $failureReason" -ForegroundColor Red
        if ($output) {
            Write-Host "  Output:" -ForegroundColor Yellow
            foreach ($line in $output | Select-Object -First 5) {
                Write-Host "    $line" -ForegroundColor Yellow
            }
        }
        $script:failed++
    }

    return @{
        Name = $Name
        Passed = $testPassed
        ExitCode = $exitCode
        ExpectedExitCode = $ExpectedExitCode
        FailureReason = $failureReason
    }
}

Write-Host "========================================" -ForegroundColor White
Write-Host "Validate-Manifest Tests" -ForegroundColor White
Write-Host "========================================" -ForegroundColor White
Write-Host ""

# Test 1: Valid minimal manifest
$results += Test-Fixture -Name "Valid minimal v1.2 manifest" `
    -FixturePath (Join-Path $fixturesDir 'valid-minimal.json') `
    -ExpectedExitCode 0

# Test 2: Missing schemaVersion
$results += Test-Fixture -Name "Missing schemaVersion" `
    -FixturePath (Join-Path $fixturesDir 'missing-schema-version.json') `
    -ExpectedExitCode 1 `
    -ExpectedErrorPattern 'schemaVersion'

# Test 3: Invalid errorCode format
$results += Test-Fixture -Name "Invalid errorCode format (no E2E_ prefix)" `
    -FixturePath (Join-Path $fixturesDir 'invalid-error-code.json') `
    -ExpectedExitCode 1 `
    -ExpectedErrorPattern 'errorCode|INVALID_FORMAT'

# Test 4: Corrupt JSON
$results += Test-Fixture -Name "Corrupt JSON" `
    -FixturePath (Join-Path $fixturesDir 'corrupt.json') `
    -ExpectedExitCode 1 `
    -ExpectedErrorPattern 'JSON|parse|error'

# Test 5: Structural validator with valid manifest (should return 2 with warning)
$results += Test-Fixture -Name "Structural validator returns exit 2 with warning" `
    -FixturePath (Join-Path $fixturesDir 'valid-minimal.json') `
    -ExpectedExitCode 2 `
    -Validator 'structural'

# Test 6: Non-existent file
Write-Host "Testing: Non-existent file" -ForegroundColor Cyan
$nonExistentOutput = & pwsh -NoProfile -Command "& '$validateScript' -ManifestPath './does-not-exist.json' 2>&1; exit `$LASTEXITCODE"
$nonExistentExit = $LASTEXITCODE
if ($nonExistentExit -eq 1) {
    Write-Host "  PASS" -ForegroundColor Green
    $passed++
}
else {
    Write-Host "  FAIL: Expected exit 1 for non-existent file, got $nonExistentExit" -ForegroundColor Red
    $failed++
}

Write-Host ""
Write-Host "========================================" -ForegroundColor White
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
Write-Host "========================================" -ForegroundColor White

if ($failed -gt 0) {
    exit 1
}
exit 0
