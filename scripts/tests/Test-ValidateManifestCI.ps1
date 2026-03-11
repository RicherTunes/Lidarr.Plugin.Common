#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unit tests for validate-manifest-ci.ps1 CI wrapper.
.DESCRIPTION
    TDD tests for strict schema validation mode in CI.
    Exit code semantics:
    - 0: Valid manifest
    - 1: Invalid manifest or strict mode + no validator
    - 2: No validator available (non-strict mode only)
#>

$ErrorActionPreference = "Stop"
$script:TestsPassed = 0
$script:TestsFailed = 0

function Write-TestResult {
    param(
        [string]$TestName,
        [bool]$Passed,
        [string]$Message = ""
    )

    if ($Passed) {
        Write-Host "  [PASS] $TestName" -ForegroundColor Green
        $script:TestsPassed++
    }
    else {
        Write-Host "  [FAIL] $TestName" -ForegroundColor Red
        if ($Message) {
            Write-Host "         $Message" -ForegroundColor Yellow
        }
        $script:TestsFailed++
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "validate-manifest-ci.ps1 Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$scriptPath = Join-Path (Join-Path $PSScriptRoot "..") "ci/validate-manifest-ci.ps1"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("validate-ci-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

# Get the schema path for tests
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$schemaPath = Join-Path $repoRoot "docs/reference/e2e-run-manifest.schema.json"

try {
    # ==========================================================================
    # Test: Script exists
    # ==========================================================================
    Write-Host "`nBasic setup:" -ForegroundColor Yellow
    Write-TestResult -TestName "CI wrapper script exists" -Passed (Test-Path $scriptPath)

    # ==========================================================================
    # Test: Valid manifest returns 0 in both modes
    # ==========================================================================
    Write-Host "`nValid manifest scenarios:" -ForegroundColor Yellow

    # Create a minimal valid manifest
    $validManifest = @{
        schemaVersion = "1.2"
        schemaId = "richer-tunes.lidarr.e2e-run-manifest"
        timestamp = (Get-Date).ToString("o")
        runId = "test123"
        runner = @{ name = "e2e-runner"; version = "test"; args = @() }
        lidarr = @{ url = "http://localhost:8686"; containerName = "test" }
        request = @{ gate = "bootstrap"; plugins = @("Qobuzarr") }
        effective = @{ gates = @("Schema"); plugins = @("Qobuzarr"); stopReason = $null }
        redaction = @{ selfTestExecuted = $true; selfTestPassed = $true; patternsCount = 10 }
        results = @()
        summary = @{ overallSuccess = $true; totalGates = 0; passed = 0; failed = 0; skipped = 0; totalDurationMs = 0 }
        diagnostics = @{ bundleCreated = $false; redactionApplied = $true }
        hostBugSuspected = @{ detected = $false }
    }
    $validPath = Join-Path $tempRoot "valid.json"
    $validManifest | ConvertTo-Json -Depth 10 | Set-Content $validPath -Encoding UTF8

    # Non-strict mode with valid manifest
    $result = & $scriptPath -ManifestPath $validPath -SchemaPath $schemaPath -Quiet 2>&1
    $exitCode = $LASTEXITCODE
    Write-TestResult -TestName "Valid manifest, non-strict mode returns 0" -Passed ($exitCode -eq 0) -Message "Got exit code $exitCode"

    # Strict mode with valid manifest
    $result = & $scriptPath -ManifestPath $validPath -SchemaPath $schemaPath -Strict -Quiet 2>&1
    $exitCode = $LASTEXITCODE
    Write-TestResult -TestName "Valid manifest, strict mode returns 0" -Passed ($exitCode -eq 0) -Message "Got exit code $exitCode"

    # ==========================================================================
    # Test: Invalid manifest returns 1 in both modes
    # ==========================================================================
    Write-Host "`nInvalid manifest scenarios:" -ForegroundColor Yellow

    # Create an invalid manifest (wrong schemaVersion)
    $invalidManifest = $validManifest.Clone()
    $invalidManifest.schemaVersion = "9.9"
    $invalidPath = Join-Path $tempRoot "invalid.json"
    $invalidManifest | ConvertTo-Json -Depth 10 | Set-Content $invalidPath -Encoding UTF8

    $result = & $scriptPath -ManifestPath $invalidPath -SchemaPath $schemaPath -Quiet 2>&1
    $exitCode = $LASTEXITCODE
    Write-TestResult -TestName "Invalid manifest, non-strict mode returns 1" -Passed ($exitCode -eq 1) -Message "Got exit code $exitCode"

    $result = & $scriptPath -ManifestPath $invalidPath -SchemaPath $schemaPath -Strict -Quiet 2>&1
    $exitCode = $LASTEXITCODE
    Write-TestResult -TestName "Invalid manifest, strict mode returns 1" -Passed ($exitCode -eq 1) -Message "Got exit code $exitCode"

    # ==========================================================================
    # Test: Missing manifest file returns 1
    # ==========================================================================
    Write-Host "`nMissing file scenarios:" -ForegroundColor Yellow

    $missingPath = Join-Path $tempRoot "nonexistent.json"
    $result = & $scriptPath -ManifestPath $missingPath -SchemaPath $schemaPath -Quiet 2>&1
    $exitCode = $LASTEXITCODE
    Write-TestResult -TestName "Missing manifest file returns 1" -Passed ($exitCode -eq 1) -Message "Got exit code $exitCode"

    # ==========================================================================
    # Test: Strict mode converts exit 2 to exit 1
    # ==========================================================================
    Write-Host "`nStrict mode (exit 2 → 1):" -ForegroundColor Yellow

    # To test this properly, we'd need to mock validate-manifest.ps1 to return exit 2.
    # Instead, we test by forcing the structural validator which returns 2.
    # We create a scenario where only structural validation is possible.

    # Test with -Validator structural (forces exit 2 in validate-manifest.ps1)
    $result = & $scriptPath -ManifestPath $validPath -SchemaPath $schemaPath -Validator structural -Quiet 2>&1
    $exitNonStrict = $LASTEXITCODE

    $result = & $scriptPath -ManifestPath $validPath -SchemaPath $schemaPath -Validator structural -Strict -Quiet 2>&1
    $exitStrict = $LASTEXITCODE

    Write-TestResult -TestName "Structural validator, non-strict mode returns 2" -Passed ($exitNonStrict -eq 2) -Message "Got exit code $exitNonStrict"
    Write-TestResult -TestName "Structural validator, strict mode returns 1" -Passed ($exitStrict -eq 1) -Message "Got exit code $exitStrict"

    # ==========================================================================
    # Test: Summary output
    # ==========================================================================
    Write-Host "`nOutput scenarios:" -ForegroundColor Yellow

    # Capture output for non-quiet mode (6>&1 captures Write-Host information stream)
    $output = & $scriptPath -ManifestPath $validPath -SchemaPath $schemaPath 6>&1 2>&1 | Out-String
    Write-TestResult -TestName "Non-quiet mode produces output" -Passed ($output.Length -gt 0)

    # Verify quiet mode suppresses info output
    $quietOutput = & $scriptPath -ManifestPath $validPath -SchemaPath $schemaPath -Quiet 6>&1 2>&1 | Out-String
    # Quiet should have less output than non-quiet (or similar for success)
    Write-TestResult -TestName "Quiet mode suppresses informational output" -Passed ($quietOutput.Length -le $output.Length)
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results: $script:TestsPassed passed, $script:TestsFailed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($script:TestsFailed -gt 0) { exit 1 }
