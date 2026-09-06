#!/usr/bin/env pwsh
# Synthetic metadata fixtures validate the packager, never substitute for Lidarr
# in a plugin build or runtime test. Production coexistence uses the real image.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $root 'tools/PluginPack.psm1') -Force
$temp = Join-Path ([IO.Path]::GetTempPath()) "host-requirements-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temp | Out-Null
$passed = 0; $failed = 0
function Check {
    param([string]$Name, [scriptblock]$Body)
    try {
        if (-not (& $Body)) { throw 'Assertion returned false.' }
        Write-Host "PASS $Name"; $script:passed++
    } catch { Write-Host "FAIL ${Name}: $($_.Exception.Message)"; $script:failed++ }
}
function New-Package {
    param([string]$Name, [string]$Minimum = '3.1.3.4970', [switch]$Missing, [switch]$Corrupt, [switch]$Duplicate)
    $path = Join-Path $temp "$Name.zip"
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $manifest = @{ id='hostfixture'; version='1.2.3'; targetFramework='net8.0'; minHostVersion=$Minimum; main='Lidarr.Plugin.HostFixture.dll' } | ConvertTo-Json -Compress
        $metadata = @{ packageId='hostfixture'; version='1.2.3'; framework='net8.0' } | ConvertTo-Json -Compress
        foreach ($pair in @(@('plugin.json',$manifest),@('package-metadata.json',$metadata))) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($pair[0]).Open())
            try { $writer.Write($pair[1]) } finally { $writer.Dispose() }
        }
        if (-not $Missing) {
            $count = if ($Duplicate) { 2 } else { 1 }
            for ($i=0; $i -lt $count; $i++) {
                $stream = $zip.CreateEntry('Lidarr.Plugin.HostFixture.dll').Open()
                try {
                    $bytes = if ($Corrupt) { [byte[]]@(1,2,3,4) } else { [IO.File]::ReadAllBytes($script:FixtureDll) }
                    $stream.Write($bytes,0,$bytes.Length)
                } finally { $stream.Dispose() }
            }
        }
    } finally { $zip.Dispose() }
    return $path
}
function Is-Rejected {
    param([string]$Path, [version]$HostVersion, [string]$Pattern)
    try {
        Assert-PluginPackageIdentity -ZipPath $Path -ValidateHostRequirements -HostVersion $HostVersion
        $false
    } catch { $_.Exception.Message -match $Pattern }
}
try {
    $hostDir = Join-Path $temp 'host'
    $pluginDir = Join-Path $temp 'plugin'
    New-Item -ItemType Directory -Path $hostDir, $pluginDir | Out-Null
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Lidarr.Core</AssemblyName><AssemblyVersion>3.1.3.4970</AssemblyVersion></PropertyGroup></Project>' | Set-Content (Join-Path $hostDir 'Host.csproj')
    'public class HostMarker {}' | Set-Content (Join-Path $hostDir 'Host.cs')
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Lidarr.Plugin.HostFixture</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="../host/Host.csproj" /></ItemGroup></Project>' | Set-Content (Join-Path $pluginDir 'Plugin.csproj')
    'public class PluginMarker : HostMarker {}' | Set-Content (Join-Path $pluginDir 'Plugin.cs')
    & dotnet build (Join-Path $pluginDir 'Plugin.csproj') -c Release -m:1 --nologo --verbosity quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Metadata fixture compilation failed; no regression verdict available.' }
    $hostIdentity = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $hostDir 'bin/Release/net8.0/Lidarr.Core.dll'))
    if ($hostIdentity.Name -cne 'Lidarr.Core' -or $hostIdentity.Version -ne [version]'3.1.3.4970') { throw 'Invalid metadata fixture identity.' }
    $script:FixtureDll = Join-Path $pluginDir 'bin/Release/net8.0/Lidarr.Plugin.HostFixture.dll'
    Check 'declared minimum cannot understate actual Lidarr assembly references' {
        Is-Rejected (New-Package 'underdeclared' -Minimum '3.0.0.4855') -Pattern 'Host requirement.*understates'
    }
    Check 'a minimum covering the real reference is accepted' {
        Assert-PluginPackageIdentity -ZipPath (New-Package 'valid') -ValidateHostRequirements
        $true
    }
    Check 'an older selected runtime is rejected explicitly' {
        Is-Rejected (New-Package 'old-runtime') -HostVersion '3.1.2.4913' -Pattern 'Host requirement.*selected host'
    }
    Check 'the exact referenced runtime is accepted' {
        Assert-PluginPackageIdentity -ZipPath (New-Package 'exact-runtime') -ValidateHostRequirements -HostVersion '3.1.3.4970'
        $true
    }
    Check 'a newer selected runtime satisfies the version floor' {
        Assert-PluginPackageIdentity -ZipPath (New-Package 'new-runtime') -ValidateHostRequirements -HostVersion '3.1.4.5000'
        $true
    }
    Check 'a deliberately stricter manifest minimum remains enforced' {
        Is-Rejected (New-Package 'strict-minimum' -Minimum '3.1.4.5000') -HostVersion '3.1.3.4970' -Pattern 'Host requirement.*selected host'
    }
    Check 'invalid minimum version fails closed' {
        Is-Rejected (New-Package 'invalid-minimum' -Minimum 'unknown') -Pattern 'Host requirement.*minimum'
    }
    Check 'missing main DLL is not a compatibility pass' {
        Is-Rejected (New-Package 'missing-dll' -Missing) -Pattern 'Host requirement.*main assembly'
    }
    Check 'ambiguous main DLL entries are rejected' {
        Is-Rejected (New-Package 'duplicate-dll' -Duplicate) -Pattern 'Host requirement.*main assembly'
    }
    Check 'unreadable managed metadata is rejected without loading code' {
        Is-Rejected (New-Package 'corrupt-dll' -Corrupt) -Pattern 'Host requirement.*metadata'
    }
    Check 'canonical packaging actually enables the host requirement gate' {
        (Get-Content (Join-Path $root 'tools/PluginPack.psm1') -Raw) -match 'Assert-PluginPackageIdentity -ZipPath \$zipPath[^\r\n]*-ValidateHostRequirements'
    }
    Check 'smoke validates declared requirements against the actual running host' {
        (Get-Content (Join-Path $root 'scripts/multi-plugin-docker-smoke-test.ps1') -Raw) -match 'Assert-PluginPackageIdentity -ZipPath \$packagePath[^\r\n]*-HostVersion \(\[version\]\$status\.version\)'
    }
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
Write-Host "Results: $passed passed, $failed failed"
if ($failed) { exit 1 }
