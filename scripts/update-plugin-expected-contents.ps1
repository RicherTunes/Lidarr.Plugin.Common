#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build a plugin ZIP and update or verify packaging/expected-contents.txt.

.DESCRIPTION
    Shared wrapper around PluginPack.psm1 and generate-expected-contents.ps1.
    Plugin repositories should keep only tiny compatibility shims that pass their
    project path to this script.
#>
param(
    [string]$RepoPath = '.',
    [string]$CommonRoot,

    [Parameter(Mandatory = $true)]
    [string]$Csproj,

    [string]$Manifest = 'plugin.json',
    [string]$Framework = 'net8.0',
    [string]$Configuration = 'Release',
    [string]$ExpectedContentsFile = 'packaging/expected-contents.txt',
    [switch]$ResolveEntryPoints,
    [switch]$RequireCanonicalAbstractions,
    [string]$CanonicalAbstractionsVersion,
    [string]$CanonicalAbstractionsSha256,
    [string]$CanonicalAbstractionsPath,
    [string[]]$ExtraBuildArgs = @(),
    [switch]$Update,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Update -and $Check) {
    throw 'Specify only one of -Update or -Check.'
}

$resolvedRepoPath = Resolve-Path -LiteralPath $RepoPath
if (-not $CommonRoot) {
    $CommonRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
}
else {
    $CommonRoot = Resolve-Path -LiteralPath $CommonRoot
}

Push-Location $resolvedRepoPath
try {
    $generator = Join-Path $CommonRoot 'scripts/generate-expected-contents.ps1'
    if (-not (Test-Path -LiteralPath $generator)) {
        throw "Generator not found at $generator. Ensure Common submodule includes scripts/generate-expected-contents.ps1."
    }

    $pluginPack = Join-Path $CommonRoot 'tools/PluginPack.psm1'
    if (-not (Test-Path -LiteralPath $pluginPack)) {
        throw "PluginPack module not found at $pluginPack."
    }

    Import-Module (Resolve-Path -LiteralPath $pluginPack) -Force

    $packageArgs = @{
        Csproj = $Csproj
        Manifest = $Manifest
        Framework = $Framework
        Configuration = $Configuration
    }
    if ($ResolveEntryPoints) {
        $packageArgs['ResolveEntryPoints'] = $true
    }
    if ($RequireCanonicalAbstractions) {
        $packageArgs['RequireCanonicalAbstractions'] = $true
    }
    if ($CanonicalAbstractionsVersion) {
        $packageArgs['CanonicalAbstractionsVersion'] = $CanonicalAbstractionsVersion
    }
    if ($CanonicalAbstractionsSha256) {
        $packageArgs['CanonicalAbstractionsSha256'] = $CanonicalAbstractionsSha256
    }
    if ($CanonicalAbstractionsPath) {
        $packageArgs['CanonicalAbstractionsPath'] = $CanonicalAbstractionsPath
    }
    if ($ExtraBuildArgs.Count -gt 0) {
        $packageArgs['ExtraBuildArgs'] = $ExtraBuildArgs
    }

    $zipPath = New-PluginPackage @packageArgs | Select-Object -Last 1
    if (-not $zipPath -or -not (Test-Path -LiteralPath $zipPath)) {
        throw 'Package build failed or produced no ZIP.'
    }

    Write-Host "`nRunning expected-contents generator against: $zipPath" -ForegroundColor Cyan

    $generatorArgs = @{
        ZipPath = $zipPath
        ManifestPath = $ExpectedContentsFile
    }
    if ($Update) {
        $generatorArgs['Update'] = $true
    }
    if ($Check) {
        $generatorArgs['Check'] = $true
    }

    & $generator @generatorArgs
    $lastExitCodeVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    if ($lastExitCodeVariable -and $null -ne $lastExitCodeVariable.Value -and $lastExitCodeVariable.Value -ne 0) {
        exit $lastExitCodeVariable.Value
    }
}
finally {
    Pop-Location
}
