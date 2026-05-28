#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails (exit 1) if any forbidden host-provided DLL is present in the plugin
    package output directory.
.DESCRIPTION
    Reads versionContract.forbiddenPackageContents from parity-spec.json and checks
    the output directory for each entry. Forbidden DLLs are host-provided assemblies
    that cause COR_E_INVALIDOPERATION type-identity conflicts when multiple plugins
    are installed simultaneously.

    Extracted from an inline MSBuild Exec command. Inline pwsh commands break on
    Linux because MSBuild runs them through /bin/sh, which expands the PowerShell
    `$variable` sigils as (empty) shell variables before pwsh parses them, corrupting
    the command. Passing the paths as -File parameters avoids that entirely.
.OUTPUTS
    Exit 0 when the package closure is clean; exit 1 (with Write-Error) when forbidden
    DLLs are found.
#>
param(
    [Parameter(Mandatory = $true)][string]$ParitySpecPath,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = 'Stop'

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
    Write-Error ('FORBIDDEN DLLs in plugin output: ' + ($found -join ', ') + '. Add PrivateAssets=all ExcludeAssets=runtime to the PackageReference or ILRepack-merge them. See docs/MULTI_PLUGIN_ALC_VALIDATION.md')
    exit 1
}

Write-Host ('[ValidatePackageClosure] OK - no forbidden DLLs found in ' + $OutputDir)
exit 0
