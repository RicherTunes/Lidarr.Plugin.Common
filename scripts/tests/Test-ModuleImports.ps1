Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$libDir = Join-Path $PSScriptRoot '../lib'

$moduleFiles = @(
    'e2e-gates.psm1',
    'e2e-json-output.psm1',
    'e2e-diagnostics.psm1',
    'e2e-error-codes.psm1',
    'e2e-error-classifier.psm1',
    'e2e-component-ids.psm1',
    'e2e-host-capabilities.psm1',
    'e2e-abstractions.psm1'
)

foreach ($moduleFile in $moduleFiles) {
    $path = Join-Path $libDir $moduleFile
    if (-not (Test-Path $path)) { continue }
    Import-Module $path -Force
}

# CRITICAL: Verify explicit error code helpers are exported from e2e-gates.psm1
# This prevents the PR #243 regression where New-ApiTimeoutDetails wasn't discoverable
# because Test-ExplicitErrorCodes.ps1 didn't import e2e-gates.psm1

$requiredGatesExports = @(
    'New-ApiTimeoutDetails',
    'New-ImportFailedDetails',
    'New-ConfigInvalidDetails',
    'Get-FoundIndexerNamesDetails'
)

$failed = 0
foreach ($fn in $requiredGatesExports) {
    $cmd = Get-Command -Name $fn -ErrorAction SilentlyContinue
    if (-not $cmd) {
        Write-Host "  [FAIL] e2e-gates.psm1 must export: $fn" -ForegroundColor Red
        $failed++
    } else {
        Write-Host "  [PASS] e2e-gates.psm1 exports: $fn" -ForegroundColor Green
    }
}

if ($failed -gt 0) {
    throw "Module export validation failed: $failed required functions not exported"
}

Write-Host 'PASS: Test-ModuleImports'
