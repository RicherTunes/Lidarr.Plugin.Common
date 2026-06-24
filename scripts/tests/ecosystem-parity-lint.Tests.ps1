#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for ecosystem-parity-lint.ps1 version-contract validation.

.DESCRIPTION
    Phase 0.3 of the stabilization program: the parity spec must encode a
    canonical commonVersion (derived from Common's Directory.Build.props at runtime)
    and the lint must catch four classes of drift in plugin repos:

      1. plugin.json.commonVersion != Common's canonical <Version>
      2. plugin.json.version != manifest.json.version (intra-repo drift)
      3. plugin.json.targetFramework != "net8.0"
      4. manifest.json targetFrameworks contains a forbidden framework

    These tests use temporary fixture directories — no real plugin repos
    are touched.
#>

BeforeAll {
    $script:CommonRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
    $script:LintScript = Join-Path $script:CommonRoot 'scripts' 'ecosystem-parity-lint.ps1'
    $script:SpecFile   = Join-Path $script:CommonRoot 'scripts' 'parity-spec.json'
    $script:CommonVersionProps = Join-Path $script:CommonRoot 'Directory.Build.props'

    # Resolve canonical version from Directory.Build.props at test time; this is
    # the single source of truth the lint must consult.
    $propsContent = Get-Content $script:CommonVersionProps -Raw
    if ($propsContent -notmatch '<Version>([^<]+)</Version>') {
        throw "Could not parse <Version> from $script:CommonVersionProps"
    }
    $script:CanonicalCommonVersion = $matches[1]

    function New-PluginFixture {
        param(
            [string]$PluginJsonVersion = '1.0.0',
            [string]$PluginJsonCommonVersion = $script:CanonicalCommonVersion,
            [string]$ManifestVersion = '1.0.0',
            [string[]]$ManifestTargetFrameworks = @('net8.0'),
            [string]$TargetFramework = 'net8.0',
            [switch]$NoManifest
        )
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) "parity-fixture-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null

        $pluginJson = [pscustomobject]@{
            id              = 'testplugin'
            apiVersion      = '1.x'
            name            = 'TestPlugin'
            version         = $PluginJsonVersion
            author          = 'test'
            description     = 'test plugin'
            homepage        = 'https://example.com'
            license         = 'MIT'
            tags            = @('test')
            commonVersion   = $PluginJsonCommonVersion
            minHostVersion  = '2.14.2.4786'
            targetFramework = $TargetFramework
            main            = 'Lidarr.Plugin.TestPlugin.dll'
            rootNamespace   = 'TestPlugin'
        }
        $pluginJson | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $dir 'plugin.json')

        if (-not $NoManifest) {
            $manifest = [pscustomobject]@{
                id               = 'testplugin'
                name             = 'TestPlugin'
                version          = $ManifestVersion
                apiVersion       = '1.x'
                minHostVersion   = '2.14.2.4786'
                assemblies       = @('Lidarr.Plugin.TestPlugin.dll')
                targetFrameworks = $ManifestTargetFrameworks
                commonVersion    = $PluginJsonCommonVersion
            }
            $manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $dir 'manifest.json')
        }

        return $dir
    }

    function Invoke-Lint {
        param([string]$RepoPath, [string]$Scope = 'VersionContract')
        $output = & pwsh -NoProfile -File $script:LintScript -RepoPath $RepoPath -Mode ci -Check $Scope -CommonRoot $script:CommonRoot 2>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
}

Describe 'parity-spec.json — version contract section' {

    It 'declares a versionContract object' {
        $spec = Get-Content $script:SpecFile -Raw | ConvertFrom-Json
        $spec.PSObject.Properties.Name | Should -Contain 'versionContract'
    }

    It 'versionContract.commonVersionSource points to Directory.Build.props' {
        $spec = Get-Content $script:SpecFile -Raw | ConvertFrom-Json
        $spec.versionContract.commonVersionSource | Should -Be 'Directory.Build.props'
    }

    It 'versionContract.targetFramework is net8.0' {
        $spec = Get-Content $script:SpecFile -Raw | ConvertFrom-Json
        $spec.versionContract.targetFramework | Should -Be 'net8.0'
    }

    It 'versionContract.forbiddenPackageContents covers host-provided assemblies' {
        $spec = Get-Content $script:SpecFile -Raw | ConvertFrom-Json
        $forbidden = $spec.versionContract.forbiddenPackageContents
        $forbidden | Should -Contain 'FluentValidation.dll'
        $forbidden | Should -Contain 'NLog.dll'
        $forbidden | Should -Contain 'System.Text.Json.dll'
        $forbidden | Should -Contain 'Lidarr.Plugin.Abstractions.dll'
        $forbidden | Should -Contain 'Lidarr.Plugin.Common.dll'
    }
}

Describe 'ecosystem-parity-lint.ps1 — version-contract violations' {

    It 'passes a fixture aligned to canonical commonVersion' {
        $repo = New-PluginFixture -PluginJsonVersion '1.0.0' -ManifestVersion '1.0.0'
        try {
            $result = Invoke-Lint -RepoPath $repo
            $result.ExitCode | Should -Be 0
        } finally {
            Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'flags plugin.json.commonVersion that lags Common version props' {
        $repo = New-PluginFixture -PluginJsonCommonVersion '1.5.0'
        try {
            $result = Invoke-Lint -RepoPath $repo
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'commonVersion'
            $result.Output | Should -Match $script:CanonicalCommonVersion
        } finally {
            Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'flags plugin.json.version != manifest.json.version drift' {
        $repo = New-PluginFixture -PluginJsonVersion '1.4.0' -ManifestVersion '1.3.2'
        try {
            $result = Invoke-Lint -RepoPath $repo
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'plugin\.json.*manifest\.json|manifest.*version'
        } finally {
            Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'flags non-net8.0 targetFramework in plugin.json' {
        $repo = New-PluginFixture -TargetFramework 'net6.0'
        try {
            $result = Invoke-Lint -RepoPath $repo
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'targetFramework'
        } finally {
            Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'flags net6.0 inside manifest.json targetFrameworks' {
        $repo = New-PluginFixture -ManifestTargetFrameworks @('net8.0','net6.0')
        try {
            $result = Invoke-Lint -RepoPath $repo
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'net6\.0|forbidden'
        } finally {
            Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'flags forbidden field "minimumVersion" in plugin.json' {
        $repo = New-PluginFixture
        # Inject the forbidden field by rewriting plugin.json
        $pj = Get-Content (Join-Path $repo 'plugin.json') -Raw | ConvertFrom-Json
        $pj | Add-Member -NotePropertyName 'minimumVersion' -NotePropertyValue '2.14.2.4786' -Force
        $pj | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $repo 'plugin.json')
        try {
            $result = Invoke-Lint -RepoPath $repo
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'minimumVersion'
        } finally {
            Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'flags forbidden field "minimumVersion" in manifest.json' {
        # Adversarial review surfaced: brainarr/manifest.json had minimumVersion and the
        # original lint missed it because the existing forbiddenFields config was dead code.
        $repo = New-PluginFixture
        $manPath = Join-Path $repo 'manifest.json'
        $man = Get-Content $manPath -Raw | ConvertFrom-Json
        $man | Add-Member -NotePropertyName 'minimumVersion' -NotePropertyValue '2.14.2.4786' -Force
        $man | ConvertTo-Json -Depth 6 | Set-Content $manPath
        try {
            $result = Invoke-Lint -RepoPath $repo
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'minimumVersion'
            $result.Output | Should -Match 'manifest\.json'
        } finally {
            Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'rejects plugin.json with malformed JSON' {
        $repo = Join-Path ([System.IO.Path]::GetTempPath()) "parity-fixture-bad-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $repo -Force | Out-Null
        Set-Content (Join-Path $repo 'plugin.json') -Value '{ this is not valid json'
        try {
            $result = Invoke-Lint -RepoPath $repo
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'parse|JSON|invalid'
        } finally {
            Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'flags plugin.json that omits commonVersion entirely' {
        $repo = Join-Path ([System.IO.Path]::GetTempPath()) "parity-fixture-nocv-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $repo -Force | Out-Null
        $pj = [pscustomobject]@{
            id = 'testplugin'
            name = 'TestPlugin'
            version = '1.0.0'
            targetFramework = 'net8.0'
        }
        $pj | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $repo 'plugin.json')
        try {
            $result = Invoke-Lint -RepoPath $repo
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'commonVersion'
        } finally {
            Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'tolerates plugins without a separate manifest.json (qobuzarr/tidalarr pattern)' {
        $repo = New-PluginFixture -NoManifest
        try {
            $result = Invoke-Lint -RepoPath $repo
            # Should not fail just because manifest.json is absent — plugin.json is enough.
            $result.ExitCode | Should -Be 0
        } finally {
            Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
