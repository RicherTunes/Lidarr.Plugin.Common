$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Golden Manifest Fixtures" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$schemaPath = Join-Path $repoRoot 'docs/reference/e2e-run-manifest.schema.json'
$fixturesDir = Join-Path $PSScriptRoot 'fixtures/golden-manifests'

if (-not (Test-Path $schemaPath)) {
    throw "Schema not found: $schemaPath"
}
if (-not (Test-Path $fixturesDir)) {
    throw "Fixtures dir not found: $fixturesDir"
}

$passed = 0
$failed = 0

function Assert-True {
    param([string]$Name, [bool]$Condition)
    if (-not $Condition) {
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        $script:failed++
    }
    else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

function Assert-Equal {
    param([string]$Name, $Actual, $Expected)
    if ($Actual -ne $Expected) {
        Write-Host "  [FAIL] $Name (expected=$Expected actual=$Actual)" -ForegroundColor Red
        $script:failed++
    }
    else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

function Validate-Fixture {
    param([string]$FileName)

    $path = Join-Path $fixturesDir $FileName
    Assert-True -Name "Fixture exists: $FileName" -Condition (Test-Path $path)

    $json = Get-Content -Path $path -Raw
    $manifest = $json | ConvertFrom-Json -Depth 100

    Assert-Equal -Name "$FileName schemaVersion" -Actual $manifest.schemaVersion -Expected '1.2'
    Assert-Equal -Name "$FileName schemaId" -Actual $manifest.schemaId -Expected 'richer-tunes.lidarr.e2e-run-manifest'

    # Full schema validation (PowerShell 7.3+)
    $schemaOk = Test-Json -Json $json -SchemaFile $schemaPath
    Assert-True -Name "$FileName schema-valid" -Condition $schemaOk

    return $manifest
}

$pass = Validate-Fixture -FileName 'pass.json'
Assert-Equal -Name "pass.json overallSuccess" -Actual $pass.summary.overallSuccess -Expected $true

$authMissing = Validate-Fixture -FileName 'auth-missing.json'
Assert-True -Name "auth-missing.json has E2E_AUTH_MISSING" -Condition ($authMissing.results.errorCode -contains 'E2E_AUTH_MISSING')

$ambiguous = Validate-Fixture -FileName 'component-ambiguous.json'
Assert-True -Name "component-ambiguous.json has E2E_COMPONENT_AMBIGUOUS" -Condition ($ambiguous.results.errorCode -contains 'E2E_COMPONENT_AMBIGUOUS')
Assert-True -Name "component-ambiguous.json has candidateIds" -Condition (($ambiguous.results | Where-Object { $_.errorCode -eq 'E2E_COMPONENT_AMBIGUOUS' }).details.candidateIds.Count -gt 1)

$hostBug = Validate-Fixture -FileName 'host-bug-alc.json'
Assert-Equal -Name "host-bug-alc.json hostBugSuspected.detected" -Actual $hostBug.hostBugSuspected.detected -Expected $true
Assert-Equal -Name "host-bug-alc.json hostBugSuspected.classification" -Actual $hostBug.hostBugSuspected.classification -Expected 'ALC'

$schemaMissing = Validate-Fixture -FileName 'schema-missing-implementation.json'
Assert-True -Name "schema-missing-implementation.json has E2E_SCHEMA_MISSING_IMPLEMENTATION" -Condition ($schemaMissing.results.errorCode -contains 'E2E_SCHEMA_MISSING_IMPLEMENTATION')

Write-Host ""
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor Red

if ($failed -gt 0) {
    throw "Golden manifest fixture tests failed: $failed"
}
