#!/usr/bin/env pwsh
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pluginPackModule = Join-Path $PSScriptRoot '../../tools/PluginPack.psm1'
$failed = 0
$testRoot = $null

function Write-Pass([string]$Message) { Write-Host "  [PASS] $Message" -ForegroundColor Green }
function Write-Fail([string]$Message) { Write-Host "  [FAIL] $Message" -ForegroundColor Red; $script:failed++ }

try {
    Write-Host '===============================================' -ForegroundColor Cyan
    Write-Host 'Test-PluginPackMergedPolicy' -ForegroundColor Cyan
    Write-Host '===============================================' -ForegroundColor Cyan

    if (-not (Test-Path -LiteralPath $pluginPackModule)) {
        throw "PluginPack module not found at $pluginPackModule"
    }

    Import-Module $pluginPackModule -Force

    $testRoot = Join-Path ([IO.Path]::GetTempPath()) "pluginpack-merged-policy-$(New-Guid)"
    $publishPath = Join-Path $testRoot 'publish'
    New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

    foreach ($name in @(
        'Lidarr.Plugin.Test.dll',
        'Lidarr.Plugin.Abstractions.dll',
        'Lidarr.Plugin.Common.dll',
        'FluentValidation.dll'
    )) {
        Set-Content -LiteralPath (Join-Path $publishPath $name) -Value 'placeholder' -Encoding UTF8
    }

    Write-Host "`n[TEST] Invoke-PluginCleanup removes merged sidecars..." -ForegroundColor Cyan
    try {
        Invoke-PluginCleanup -PublishPath $publishPath -AssemblyName 'Lidarr.Plugin.Test'

        foreach ($name in @('Lidarr.Plugin.Abstractions.dll', 'Lidarr.Plugin.Common.dll', 'FluentValidation.dll')) {
            if (Test-Path -LiteralPath (Join-Path $publishPath $name)) {
                Write-Fail "$name should be removed from merged plugin package output"
            }
        }

        if (Test-Path -LiteralPath (Join-Path $publishPath 'Lidarr.Plugin.Test.dll')) {
            Write-Pass 'main plugin DLL kept and merged sidecars removed'
        } else {
            Write-Fail 'main plugin DLL should be kept'
        }
    }
    catch {
        Write-Fail "Invoke-PluginCleanup threw unexpectedly: $($_.Exception.Message)"
    }

    Write-Host "`n[TEST] Invoke-PluginCleanup rejects explicit keep of merged sidecars..." -ForegroundColor Cyan
    try {
        Set-Content -LiteralPath (Join-Path $publishPath 'Lidarr.Plugin.Abstractions.dll') -Value 'placeholder' -Encoding UTF8
        Invoke-PluginCleanup -PublishPath $publishPath -AssemblyName 'Lidarr.Plugin.Test' -AdditionalKeep @('Lidarr.Plugin.Abstractions.dll')
        Write-Fail 'Expected AdditionalKeep for Lidarr.Plugin.Abstractions.dll to throw'
    }
    catch {
        if ($_.Exception.Message -match 'forbidden|sidecar|Abstractions') {
            Write-Pass 'explicit Abstractions sidecar keep was rejected'
        } else {
            Write-Fail "Unexpected rejection message: $($_.Exception.Message)"
        }
    }

    Write-Host "`n[TEST] Assert-PluginAssemblyHasNoMergedReferences rejects unmerged assembly references..." -ForegroundColor Cyan
    try {
        $fixtureRoot = Join-Path $testRoot 'assembly-reference-fixture'
        $abstractionsProject = Join-Path $fixtureRoot 'Abstractions'
        $badProject = Join-Path $fixtureRoot 'BadPlugin'
        $cleanProject = Join-Path $fixtureRoot 'CleanPlugin'
        $abstractionsOut = Join-Path $fixtureRoot 'abstractions-out'
        $badOut = Join-Path $fixtureRoot 'bad-out'
        $cleanOut = Join-Path $fixtureRoot 'clean-out'
        New-Item -ItemType Directory -Path $abstractionsProject, $badProject, $cleanProject -Force | Out-Null

        Set-Content -LiteralPath (Join-Path $abstractionsProject 'Abstractions.csproj') -Encoding UTF8 -Value @(
            '<Project Sdk="Microsoft.NET.Sdk">',
            '  <PropertyGroup>',
            '    <TargetFramework>net8.0</TargetFramework>',
            '    <AssemblyName>Lidarr.Plugin.Abstractions</AssemblyName>',
            '  </PropertyGroup>',
            '</Project>'
        )
        Set-Content -LiteralPath (Join-Path $abstractionsProject 'Marker.cs') -Encoding UTF8 -Value 'namespace Lidarr.Plugin.Abstractions.Contracts { public interface IPluginMarker {} }'
        $null = & dotnet build (Join-Path $abstractionsProject 'Abstractions.csproj') -c Release -o $abstractionsOut -nologo -v:quiet
        if ($LASTEXITCODE -ne 0) { throw 'Failed to build synthetic Abstractions fixture' }
        $abstractionsDll = Join-Path $abstractionsOut 'Lidarr.Plugin.Abstractions.dll'

        Set-Content -LiteralPath (Join-Path $badProject 'BadPlugin.csproj') -Encoding UTF8 -Value @(
            '<Project Sdk="Microsoft.NET.Sdk">',
            '  <PropertyGroup>',
            '    <TargetFramework>net8.0</TargetFramework>',
            '    <AssemblyName>Lidarr.Plugin.Test.Bad</AssemblyName>',
            '  </PropertyGroup>',
            '  <ItemGroup>',
            '    <Reference Include="Lidarr.Plugin.Abstractions">',
            "      <HintPath>$abstractionsDll</HintPath>",
            '    </Reference>',
            '  </ItemGroup>',
            '</Project>'
        )
        Set-Content -LiteralPath (Join-Path $badProject 'Bad.cs') -Encoding UTF8 -Value 'using Lidarr.Plugin.Abstractions.Contracts; namespace PluginPackMergedPolicy { public sealed class Bad : IPluginMarker {} }'
        $null = & dotnet build (Join-Path $badProject 'BadPlugin.csproj') -c Release -o $badOut -nologo -v:quiet
        if ($LASTEXITCODE -ne 0) { throw 'Failed to build synthetic bad plugin fixture' }
        $badPluginDll = Join-Path $badOut 'Lidarr.Plugin.Test.Bad.dll'

        Set-Content -LiteralPath (Join-Path $cleanProject 'CleanPlugin.csproj') -Encoding UTF8 -Value @(
            '<Project Sdk="Microsoft.NET.Sdk">',
            '  <PropertyGroup>',
            '    <TargetFramework>net8.0</TargetFramework>',
            '    <AssemblyName>Lidarr.Plugin.Test.Clean</AssemblyName>',
            '  </PropertyGroup>',
            '</Project>'
        )
        Set-Content -LiteralPath (Join-Path $cleanProject 'Clean.cs') -Encoding UTF8 -Value 'namespace PluginPackMergedPolicy { public sealed class Clean {} }'
        $null = & dotnet build (Join-Path $cleanProject 'CleanPlugin.csproj') -c Release -o $cleanOut -nologo -v:quiet
        if ($LASTEXITCODE -ne 0) { throw 'Failed to build synthetic clean plugin fixture' }
        $cleanPluginDll = Join-Path $cleanOut 'Lidarr.Plugin.Test.Clean.dll'

        try {
            Assert-PluginAssemblyHasNoMergedReferences -AssemblyPath $badPluginDll
            Write-Fail 'Expected plugin DLL with external Abstractions reference to be rejected'
        }
        catch {
            if ($_.Exception.Message -match 'Lidarr\.Plugin\.Abstractions|merged assembly reference') {
                Write-Pass 'external Abstractions assembly reference was rejected'
            } else {
                Write-Fail "Unexpected reference rejection message: $($_.Exception.Message)"
            }
        }

        try {
            Assert-PluginAssemblyHasNoMergedReferences -AssemblyPath $cleanPluginDll
            Write-Pass 'clean plugin assembly reference set accepted'
        }
        catch {
            Write-Fail "Clean plugin DLL should not be rejected: $($_.Exception.Message)"
        }
    }
    catch {
        Write-Fail "Assembly reference policy test setup failed: $($_.Exception.Message)"
    }}
finally {
    if ($testRoot -and (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failed -gt 0) {
    Write-Host ""
    Write-Host "FAILED: $failed test(s)" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host 'All tests passed.' -ForegroundColor Green
