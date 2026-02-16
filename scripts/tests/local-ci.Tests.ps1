#Requires -Modules Pester

<#
.SYNOPSIS
    Pester contract tests for the local-ci.ps1 shared runner.

.DESCRIPTION
    Validates preflight config validation, host-path auto-detection,
    and stage orchestration behavior. Does NOT run Docker or dotnet
    (those require runtime dependencies).
#>

BeforeAll {
    $script:LocalCiScript = Join-Path $PSScriptRoot '..' 'local-ci.ps1'
}

Describe 'PREFLIGHT: Config validation' {

    It 'Fails when required config keys are missing' {
        $result = & pwsh -NoProfile -Command "
            `$ErrorActionPreference = 'SilentlyContinue'
            & '$($script:LocalCiScript)' -Config @{ RepoName = 'TestRepo' } 2>&1
            exit `$LASTEXITCODE
        "
        $LASTEXITCODE | Should -Be 1
    }

    It 'Fails with clear message listing missing keys' {
        $output = & pwsh -NoProfile -Command "
            & '$($script:LocalCiScript)' -Config @{
                RepoName = 'TestRepo'
                PluginCsproj = 'test.csproj'
            } 2>&1
        " | Out-String

        $output | Should -Match 'Missing required config keys'
        $output | Should -Match 'ManifestPath'
        $output | Should -Match 'HostAssembliesPath'
    }

    It 'Fails when CommonPath does not exist' {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "local-ci-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"

        $output = & pwsh -NoProfile -Command "
            & '$($script:LocalCiScript)' -Config @{
                RepoName            = 'TestRepo'
                PluginCsproj        = 'test.csproj'
                ManifestPath        = 'plugin.json'
                MainDll             = 'Test.dll'
                HostAssembliesPath  = '$tempDir'
                CommonPath          = '$tempDir/nonexistent-common'
                LidarrDockerVersion = 'pr-plugins-3.1.2.4913'
                ExpectedContentsFile = 'packaging/expected-contents.txt'
            } -SkipExtract 2>&1
        " | Out-String

        $output | Should -Match 'Common submodule not found'
    }
}

Describe 'PREFLIGHT: Host path auto-detection' {

    It 'Fails with remediation message when host path not found with -SkipExtract' {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "local-ci-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
        # Create a fake CommonPath with required tool stubs
        $fakeCommon = Join-Path $tempDir 'common'
        New-Item -ItemType Directory -Path (Join-Path $fakeCommon 'tools') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $fakeCommon 'scripts') -Force | Out-Null
        Set-Content -Path (Join-Path $fakeCommon 'tools/PluginPack.psm1') -Value '# stub'
        Set-Content -Path (Join-Path $fakeCommon 'scripts/generate-expected-contents.ps1') -Value '# stub'

        try {
            $output = & pwsh -NoProfile -Command "
                Set-Location '$tempDir'
                & '$($script:LocalCiScript)' -Config @{
                    RepoName            = 'TestRepo'
                    PluginCsproj        = 'test.csproj'
                    ManifestPath        = 'plugin.json'
                    MainDll             = 'Test.dll'
                    HostAssembliesPath  = 'nonexistent/path'
                    CommonPath          = '$fakeCommon'
                    LidarrDockerVersion = 'pr-plugins-3.1.2.4913'
                    ExpectedContentsFile = 'packaging/expected-contents.txt'
                } -SkipExtract 2>&1
            " | Out-String

            $output | Should -Match 'Host assemblies not found'
            $output | Should -Match 'Run without -SkipExtract'
        }
        finally {
            Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe 'Config contract: all required keys' {

    It 'Accepts a complete config without preflight errors (fails later at BUILD)' {
        # This test validates that preflight passes with all keys present.
        # It will fail at BUILD (no real project), but that's expected.
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "local-ci-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
        $fakeCommon = Join-Path $tempDir 'common'
        $fakeHost = Join-Path $tempDir 'host'
        New-Item -ItemType Directory -Path (Join-Path $fakeCommon 'tools') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $fakeCommon 'scripts') -Force | Out-Null
        New-Item -ItemType Directory -Path $fakeHost -Force | Out-Null
        Set-Content -Path (Join-Path $fakeCommon 'tools/PluginPack.psm1') -Value '# stub'
        Set-Content -Path (Join-Path $fakeCommon 'scripts/generate-expected-contents.ps1') -Value '# stub'

        try {
            $output = & pwsh -NoProfile -Command "
                Set-Location '$tempDir'
                & '$($script:LocalCiScript)' -Config @{
                    RepoName            = 'TestRepo'
                    PluginCsproj        = 'nonexistent.csproj'
                    ManifestPath        = 'plugin.json'
                    MainDll             = 'Test.dll'
                    HostAssembliesPath  = '$fakeHost'
                    CommonPath          = '$fakeCommon'
                    LidarrDockerVersion = 'pr-plugins-3.1.2.4913'
                    ExpectedContentsFile = 'packaging/expected-contents.txt'
                } -SkipExtract -SkipTests 2>&1
            " | Out-String

            # Preflight should pass - look for the banner
            $output | Should -Match 'LOCAL CI VERIFICATION: TestRepo'
            # Should NOT fail at preflight
            $output | Should -Not -Match 'PREFLIGHT FAIL'
        }
        finally {
            Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
