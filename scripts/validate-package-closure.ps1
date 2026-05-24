#!/usr/bin/env pwsh
# Validates that a plugin's build output directory does NOT contain
# forbidden DLLs (host-provided contract assemblies). Reads the
# forbidden list from parity-spec.json.
#
# Invoked from build/PluginPackaging.targets ValidatePackageClosure target.
# Lives in a .ps1 so shells (bash/cmd) don't eat PowerShell variables
# during MSBuild -> Exec -> shell -> pwsh argument marshalling.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ParitySpecPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

if (-not (Test-Path -LiteralPath $ParitySpecPath)) {
    Write-Error "Parity spec not found: $ParitySpecPath"
    exit 1
}
if (-not (Test-Path -LiteralPath $OutputDir)) {
    Write-Error "Output directory not found: $OutputDir"
    exit 1
}

$spec = Get-Content -LiteralPath $ParitySpecPath -Raw | ConvertFrom-Json
$forbidden = $spec.versionContract.forbiddenPackageContents
$found = @()
foreach ($name in $forbidden) {
    $candidate = Join-Path $OutputDir $name
    if (Test-Path -LiteralPath $candidate) {
        $found += $name
    }
}

if ($found.Count -gt 0) {
    Write-Error ("FORBIDDEN DLLs in plugin output: " + ($found -join ', ') + ". Add PrivateAssets=all ExcludeAssets=runtime to PackageReference or ILRepack-merge them. See docs/MULTI_PLUGIN_ALC_VALIDATION.md")
    exit 1
}

Write-Host "[ValidatePackageClosure] OK - no forbidden DLLs found in $OutputDir"
exit 0
