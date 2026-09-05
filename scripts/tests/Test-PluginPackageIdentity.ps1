#!/usr/bin/env pwsh
# Package identity is checked from ZIP bytes, not an independently reconstructed filename.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $root 'tools/PluginPack.psm1') -Force
$passed = 0
$failed = 0
function Test-Assertion {
    param([string]$Name, [scriptblock]$Body)
    try {
        if (-not (& $Body)) { throw 'Assertion returned false.' }
        Write-Host "PASS $Name"; $script:passed++
    } catch { Write-Host "FAIL ${Name}: $($_.Exception.Message)"; $script:failed++ }
}
function New-Fixture {
    param([string]$Directory, [string]$FileVersion = '1.2.3', [string]$ManifestVersion = '1.2.3', [string]$MetadataVersion = '1.2.3', [string]$MetadataId = 'fixture', [string]$MetadataFramework = 'net8.0', [switch]$Duplicate, [switch]$NoMetadata)
    New-Item -ItemType Directory -Path $Directory | Out-Null
    $path = Join-Path $Directory "fixture-$FileVersion-net8.0.zip"
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $manifest = @{ id = 'fixture'; version = $ManifestVersion; targetFramework = 'net8.0'; main = 'Lidarr.Plugin.Fixture.dll' } | ConvertTo-Json -Compress
        $metadata = @{ packageId = $MetadataId; version = $MetadataVersion; framework = $MetadataFramework } | ConvertTo-Json -Compress
        $entries = @(,@('plugin.json', $manifest))
        if (-not $NoMetadata) { $entries += ,@('package-metadata.json', $metadata) }
        if ($Duplicate) { $entries += ,@('plugin.json', $manifest) }
        foreach ($pair in $entries) {
            $entry = $zip.CreateEntry($pair[0])
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write($pair[1]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
    return $path
}
function Is-IdentityRejection {
    param([string]$Path, [string]$Expected = '1.2.3', [string]$MessagePattern = 'Package identity mismatch')
    try { Assert-PluginPackageIdentity -ZipPath $Path -ExpectedVersion $Expected -RequireVersionedFileName; return $false }
    catch { return $_.Exception.Message -match $MessagePattern }
}
$temp = Join-Path ([IO.Path]::GetTempPath()) "package-identity-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    Test-Assertion 'matching evaluated version, manifest, metadata, and filename pass' {
        $p = New-Fixture (Join-Path $temp 'valid')
        Assert-PluginPackageIdentity -ZipPath $p -ExpectedVersion '1.2.3' -RequireVersionedFileName
        $true
    }
    Test-Assertion 'historical dev filename around a release manifest fails' {
        Is-IdentityRejection (New-Fixture (Join-Path $temp 'wrong-name') -FileVersion '0.1.0-dev')
    }
    Test-Assertion 'stale manifest version fails even when filename and metadata agree' {
        Is-IdentityRejection (New-Fixture (Join-Path $temp 'wrong-manifest') -ManifestVersion '1.0.0')
    }
    Test-Assertion 'stale package metadata version fails' {
        Is-IdentityRejection (New-Fixture (Join-Path $temp 'wrong-metadata') -MetadataVersion '1.0.0')
    }
    Test-Assertion 'evaluated build version must match all packaged version fields' {
        Is-IdentityRejection (New-Fixture (Join-Path $temp 'wrong-build')) -Expected '9.9.9'
    }
    Test-Assertion 'package ID drift fails' {
        Is-IdentityRejection (New-Fixture (Join-Path $temp 'wrong-id') -MetadataId 'other')
    }
    Test-Assertion 'framework drift fails' {
        Is-IdentityRejection (New-Fixture (Join-Path $temp 'wrong-framework') -MetadataFramework 'net6.0')
    }
    Test-Assertion 'duplicate root manifests are ambiguous and fail' {
        Is-IdentityRejection (New-Fixture (Join-Path $temp 'duplicate') -Duplicate) -MessagePattern 'Package identity requires exactly one bounded root'
    }
    Test-Assertion 'missing package metadata fails closed' {
        Is-IdentityRejection (New-Fixture (Join-Path $temp 'missing') -NoMetadata) -MessagePattern 'Package identity requires exactly one bounded root'
    }
    Test-Assertion 'renamed user input can validate contents without a filename assertion' {
        $p = New-Fixture (Join-Path $temp 'renamed') -FileVersion 'latest'
        Assert-PluginPackageIdentity -ZipPath $p
        $true
    }
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
Write-Host "Results: $passed passed, $failed failed"
if ($failed) { exit 1 }
