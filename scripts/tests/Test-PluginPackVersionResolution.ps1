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
      - Unconditional <Version> literal            -> used directly (fast path)
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
    # ---- Case 1: unconditional literal is used directly -----------------------
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

    Test-Assertion 'unconditional <Version> literal is used directly' {
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
}
finally {
    Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
if ($failed -gt 0) { exit 1 }
exit 0
