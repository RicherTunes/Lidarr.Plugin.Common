#!/usr/bin/env pwsh
<#
.SYNOPSIS
    CI wrapper for validate-manifest.ps1 with strict mode support.
.DESCRIPTION
    Wraps validate-manifest.ps1 for CI environments where "no validator available"
    (exit code 2) should be treated as a hard failure to prevent schema drift.

    Exit code semantics:
    - 0: Manifest is valid
    - 1: Manifest is invalid OR strict mode + no validator available
    - 2: No validator available (non-strict mode only)
.PARAMETER ManifestPath
    Path to the manifest JSON file to validate.
.PARAMETER SchemaPath
    Path to the JSON schema file.
.PARAMETER Validator
    Force a specific validator: powershell, python, node, structural
.PARAMETER Strict
    Treat exit code 2 (no validator) as exit code 1 (failure).
    Use in CI to ensure schema validation never silently degrades.
.PARAMETER Quiet
    Suppress informational output.
.EXAMPLE
    ./validate-manifest-ci.ps1 -ManifestPath ./manifest.json -SchemaPath ./schema.json -Strict
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter()]
    [string]$SchemaPath,

    [Parameter()]
    [ValidateSet("powershell", "python", "node", "structural")]
    [string]$Validator,

    [Parameter()]
    [switch]$Strict,

    [Parameter()]
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

# Build arguments for validate-manifest.ps1
$validateScript = Join-Path $PSScriptRoot ".." "validate-manifest.ps1"
$validateParams = @{
    ManifestPath = $ManifestPath
}

if ($SchemaPath) {
    $validateParams.SchemaPath = $SchemaPath
}

if ($Validator) {
    $validateParams.Validator = $Validator
}
if ($Quiet) {
    $validateParams.Quiet = $true
}

# Invoke the validator
# Use try-catch to handle Write-Error from validate-manifest.ps1 (which has ErrorActionPreference = Stop)
$exitCode = 0
try {
    & $validateScript @validateParams
    $exitCode = $LASTEXITCODE
}
catch {
    # Write-Error from child script - capture and continue
    if (-not $Quiet) {
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
    $exitCode = 1
}

# In strict mode, convert exit 2 (no validator) to exit 1 (failure)
if ($Strict -and $exitCode -eq 2) {
    if (-not $Quiet) {
        Write-Host "[STRICT] No full schema validator available - treating as failure" -ForegroundColor Red
    }
    exit 1
}

exit $exitCode
