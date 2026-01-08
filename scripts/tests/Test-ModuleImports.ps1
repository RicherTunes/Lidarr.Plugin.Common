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

Write-Host 'PASS: Test-ModuleImports'
