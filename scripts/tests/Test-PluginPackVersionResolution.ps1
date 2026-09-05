#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Self-tests for PluginPack.psm1's Resolve-PluginPackVersion.

.DESCRIPTION
    The zip filename version must be the version MSBuild would actually stamp.
    The historical XML-first parse read `<Version Condition="'$(Version)' == ''">0.1.0-dev</Version>`
    fallback literals verbatim (conditions are not evaluable statically), which
    shipped a qobuzarr release candidate named 0.1.0-dev while the assembly was
    stamped 0.5.12 from the VERSION file via Directory.Build.props.

    Cases:
      - Unconditional <Version> literal            -> evaluated by MSBuild
      - CONDITIONAL <Version> fallback only        -> ignored; MSBuild evaluation wins
        (temp project with Directory.Build.props VERSION-file plumbing -> real version)
      - Unresolved MSBuild expression in <Version> -> ignored (existing behavior kept)
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $RepoRoot 'tools/PluginPack.psm1') -Force

$passed = 0
$failed = 0

function Test-Assertion {
    param([string]$Name, [scriptblock]$Test)
    Write-Host "  Testing: $Name..." -NoNewline
    try {
        if (& $Test) { Write-Host ' PASS' -ForegroundColor Green; $script:passed++ }
        else { Write-Host ' FAIL' -ForegroundColor Red; $script:failed++ }
    }
    catch {
        Write-Host " FAIL ($($_.Exception.Message))" -ForegroundColor Red
        $script:failed++
    }
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("pluginpack-version-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    # ---- Case 1: unconditional literal resolves through MSBuild ---------------
    $proj1 = Join-Path $tempRoot 'Plain'
    New-Item -ItemType Directory -Path $proj1 | Out-Null
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Version>2.3.4</Version>
  </PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $proj1 'Plain.csproj')

    Test-Assertion 'unconditional <Version> literal resolves correctly through MSBuild' {
        (Resolve-PluginPackVersion -CsprojPath (Join-Path $proj1 'Plain.csproj')) -eq '2.3.4'
    }

    # ---- Case 2: conditional fallback must NOT win over the evaluated version --
    $proj2 = Join-Path $tempRoot 'Conditional'
    New-Item -ItemType Directory -Path $proj2 | Out-Null
    '9.9.9' | Set-Content (Join-Path $proj2 'VERSION') -NoNewline
    @'
<Project>
  <PropertyGroup>
    <VersionFromFile Condition="'$(VersionFromFile)' == '' And Exists('$(MSBuildThisFileDirectory)VERSION')">$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)VERSION').Trim())</VersionFromFile>
    <Version Condition="'$(Version)' == '' And '$(VersionFromFile)' != ''">$(VersionFromFile)</Version>
  </PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $proj2 'Directory.Build.props')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Version Condition="'$(Version)' == ''">0.1.0-dev</Version>
  </PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $proj2 'Conditional.csproj')

    Test-Assertion 'conditional <Version> fallback defers to MSBuild evaluation (VERSION file wins)' {
        (Resolve-PluginPackVersion -CsprojPath (Join-Path $proj2 'Conditional.csproj')) -eq '9.9.9'
    }

    # ---- Case 3: unresolved expression is still ignored ------------------------
    $proj3 = Join-Path $tempRoot 'Expr'
    New-Item -ItemType Directory -Path $proj3 | Out-Null
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <VersionPrefix>7.7.7</VersionPrefix>
    <Version>$(VersionPrefix)</Version>
  </PropertyGroup>
</Project>
'@ | Set-Content (Join-Path $proj3 'Expr.csproj')

    Test-Assertion 'unresolved $(...) expression falls through to MSBuild evaluation' {
        (Resolve-PluginPackVersion -CsprojPath (Join-Path $proj3 'Expr.csproj')) -eq '7.7.7'
    }
    # Parent conditions and late imports cannot be evaluated from literal XML.
    $conditionalGroup = Join-Path $tempRoot 'ConditionalGroup.csproj'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><Version>2.3.4</Version></PropertyGroup>
  <PropertyGroup Condition="'never' == 'always'"><Version>9.9.9</Version></PropertyGroup>
</Project>
'@ | Set-Content $conditionalGroup
    Test-Assertion 'false parent PropertyGroup cannot override the evaluated version' {
        (Resolve-PluginPackVersion -CsprojPath $conditionalGroup) -eq '2.3.4'
    }
    $late = Join-Path $tempRoot 'Late'
    New-Item -ItemType Directory -Path $late | Out-Null
    '<Project><PropertyGroup><Version>4.5.6</Version></PropertyGroup></Project>' | Set-Content (Join-Path $late 'Directory.Build.targets')
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><Version>2.3.4</Version></PropertyGroup></Project>' | Set-Content (Join-Path $late 'Late.csproj')
    Test-Assertion 'late Directory.Build.targets version override is authoritative' {
        (Resolve-PluginPackVersion -CsprojPath (Join-Path $late 'Late.csproj')) -eq '4.5.6'
    }
    $assembly = Join-Path $tempRoot 'Assembly.csproj'
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyVersion>1.0.0.0</AssemblyVersion><VersionPrefix>7.8.9</VersionPrefix><VersionSuffix>rc-1</VersionSuffix></PropertyGroup></Project>' | Set-Content $assembly
    Test-Assertion 'package version is not AssemblyVersion and supports hyphenated prereleases' {
        (Resolve-PluginPackVersion -CsprojPath $assembly) -eq '7.8.9-rc-1'
    }
    Test-Assertion 'the same explicit Version build override reaches version evaluation' {
        (Resolve-PluginPackVersion -CsprojPath (Join-Path $proj1 'Plain.csproj') -ExtraBuildArgs @('-p:Version=6.7.8')) -eq '6.7.8'
    }
    $configuration = Join-Path $tempRoot 'Configuration.csproj'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><Version>2.2.2</Version></PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'Release'"><Version>8.8.8</Version></PropertyGroup>
</Project>
'@ | Set-Content $configuration
    Test-Assertion 'version evaluation uses the same Release configuration as packaging' {
        (Resolve-PluginPackVersion -CsprojPath $configuration -Configuration Release) -eq '8.8.8'
    }
    $ci = Join-Path $tempRoot 'Ci.csproj'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><Version>2.2.2</Version></PropertyGroup>
  <PropertyGroup Condition="'$(ContinuousIntegrationBuild)' == 'true'"><Version>5.5.5</Version></PropertyGroup>
</Project>
'@ | Set-Content $ci
    Test-Assertion 'version evaluation uses the packaging CI property defaults' {
        (Resolve-PluginPackVersion -CsprojPath $ci) -eq '5.5.5'
    }
    $broken = Join-Path $tempRoot 'Broken.csproj'
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><Import Project="does-not-exist.props" /></Project>' | Set-Content $broken
    Test-Assertion 'failed MSBuild evaluation cannot fall back to a fabricated package version' {
        try { $null = Resolve-PluginPackVersion -CsprojPath $broken; $false }
        catch { $_.Exception.Message -match 'MSBuild.*version|version.*MSBuild' }
    }

    # ---- Case 4: cleanup removes orphaned culture satellite dirs -----------------
    # `dotnet build -o <shared>` also lands the OutputItemType=Analyzer project's
    # Roslyn satellites (cs/de/... Microsoft.CodeAnalysis*.resources.dll) in the
    # publish dir; the root-level sweep never entered subdirectories, so 26
    # satellite DLLs nearly shipped in qobuzarr v0.5.12.
    $pub = Join-Path $tempRoot 'publish'
    New-Item -ItemType Directory -Path (Join-Path $pub 'cs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $pub 'de') -Force | Out-Null
    Set-Content (Join-Path $pub 'Lidarr.Plugin.Fake.dll') 'x'
    Set-Content (Join-Path $pub 'plugin.json') '{}'
    Set-Content (Join-Path $pub 'cs/Microsoft.CodeAnalysis.resources.dll') 'x'
    Set-Content (Join-Path $pub 'de/Microsoft.CodeAnalysis.CSharp.resources.dll') 'x'
    # A satellite belonging to a KEPT assembly must survive.
    Set-Content (Join-Path $pub 'cs/Lidarr.Plugin.Fake.resources.dll') 'x'

    Invoke-PluginCleanup -PublishPath $pub -AssemblyName 'Lidarr.Plugin.Fake'

    Test-Assertion 'cleanup removes orphaned culture satellites' {
        -not (Test-Path (Join-Path $pub 'cs/Microsoft.CodeAnalysis.resources.dll')) -and
        -not (Test-Path (Join-Path $pub 'de'))
    }
    Test-Assertion 'cleanup keeps satellites of kept assemblies' {
        Test-Path (Join-Path $pub 'cs/Lidarr.Plugin.Fake.resources.dll')
    }
}
finally {
    Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
if ($failed -gt 0) { exit 1 }
exit 0
