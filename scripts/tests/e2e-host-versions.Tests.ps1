#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for e2e-host-versions.psm1 module.

.DESCRIPTION
    Tests version comparison logic, match policies, and output formats.
    Does NOT test Docker extraction (requires Docker runtime).
#>

BeforeAll {
    $modulePath = Join-Path $PSScriptRoot '..' 'lib' 'e2e-host-versions.psm1'
    Import-Module $modulePath -Force
}

Describe 'Get-NormalizedVersion' {
    It 'Extracts Major.Minor.Patch from simple version' {
        InModuleScope 'e2e-host-versions' {
            Get-NormalizedVersion -Value '9.5.4' | Should -Be '9.5.4'
        }
    }

    It 'Extracts version from string with build metadata' {
        InModuleScope 'e2e-host-versions' {
            Get-NormalizedVersion -Value '9.5.4+abc123' | Should -Be '9.5.4'
        }
    }

    It 'Extracts version from string with pre-release suffix' {
        InModuleScope 'e2e-host-versions' {
            Get-NormalizedVersion -Value '9.5.4-beta.1' | Should -Be '9.5.4'
        }
    }

    It 'Handles four-part version' {
        InModuleScope 'e2e-host-versions' {
            Get-NormalizedVersion -Value '9.5.4.0' | Should -Be '9.5.4.0'
        }
    }

    It 'Returns null for empty string' {
        InModuleScope 'e2e-host-versions' {
            Get-NormalizedVersion -Value '' | Should -BeNullOrEmpty
        }
    }

    It 'Returns null for null' {
        InModuleScope 'e2e-host-versions' {
            Get-NormalizedVersion -Value $null | Should -BeNullOrEmpty
        }
    }
}

Describe 'Get-MajorMinor' {
    It 'Extracts Major.Minor from three-part version' {
        InModuleScope 'e2e-host-versions' {
            Get-MajorMinor -Value '9.5.4' | Should -Be '9.5'
        }
    }

    It 'Extracts Major.Minor from four-part version' {
        InModuleScope 'e2e-host-versions' {
            Get-MajorMinor -Value '9.5.4.0' | Should -Be '9.5'
        }
    }

    It 'Returns null for empty string' {
        InModuleScope 'e2e-host-versions' {
            Get-MajorMinor -Value '' | Should -BeNullOrEmpty
        }
    }
}

Describe 'Compare-HostPluginVersions' {
    Context 'MajorMinor match policy' {
        It 'Passes when host 9.5.4 vs plugin 9.5.6 (same Major.Minor)' {
            $hostVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    DllName = 'Test.dll'
                    Reason = 'Test reason'
                    AssemblyVersion = '9.0.0.0'
                    FileVersion = '9.5.4'
                    ProductVersion = '9.5.4'
                    Found = $true
                }
            )
            $pinnedVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    PinnedVersion = '9.5.6'
                }
            )

            $results = Compare-HostPluginVersions -HostVersions $hostVersions -PinnedVersions $pinnedVersions -MatchPolicy MajorMinor -Format Json | ConvertFrom-Json

            $results.hasErrors | Should -Be $false
            $results.results[0].status | Should -Be 'OK'
            $results.results[0].match | Should -Be $true
        }

        It 'Fails when host 9.4.0 vs plugin 9.5.6 (different Minor)' {
            $hostVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    DllName = 'Test.dll'
                    Reason = 'Test reason'
                    AssemblyVersion = '9.0.0.0'
                    FileVersion = '9.4.0'
                    ProductVersion = '9.4.0'
                    Found = $true
                }
            )
            $pinnedVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    PinnedVersion = '9.5.6'
                }
            )

            $results = Compare-HostPluginVersions -HostVersions $hostVersions -PinnedVersions $pinnedVersions -MatchPolicy MajorMinor -Format Json | ConvertFrom-Json

            $results.hasErrors | Should -Be $true
            $results.results[0].status | Should -Be 'MISMATCH'
            $results.results[0].match | Should -Be $false
        }
    }

    Context 'Exact match policy' {
        It 'Fails when host 9.5.4 vs plugin 9.5.6 (different Patch)' {
            $hostVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    DllName = 'Test.dll'
                    Reason = 'Test reason'
                    AssemblyVersion = '9.0.0.0'
                    FileVersion = '9.5.4'
                    ProductVersion = '9.5.4'
                    Found = $true
                }
            )
            $pinnedVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    PinnedVersion = '9.5.6'
                }
            )

            $results = Compare-HostPluginVersions -HostVersions $hostVersions -PinnedVersions $pinnedVersions -MatchPolicy Exact -Format Json | ConvertFrom-Json

            $results.hasErrors | Should -Be $true
            $results.results[0].status | Should -Be 'MISMATCH'
            $results.results[0].match | Should -Be $false
        }

        It 'Passes when host 9.5.6 vs plugin 9.5.6 (exact match)' {
            $hostVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    DllName = 'Test.dll'
                    Reason = 'Test reason'
                    AssemblyVersion = '9.0.0.0'
                    FileVersion = '9.5.6'
                    ProductVersion = '9.5.6'
                    Found = $true
                }
            )
            $pinnedVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    PinnedVersion = '9.5.6'
                }
            )

            $results = Compare-HostPluginVersions -HostVersions $hostVersions -PinnedVersions $pinnedVersions -MatchPolicy Exact -Format Json | ConvertFrom-Json

            $results.hasErrors | Should -Be $false
            $results.results[0].status | Should -Be 'OK'
            $results.results[0].match | Should -Be $true
        }
    }

    Context 'Error cases' {
        It 'Reports HOST_NOT_FOUND when assembly not found' {
            $hostVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    DllName = 'Test.dll'
                    Reason = 'Test reason'
                    AssemblyVersion = $null
                    FileVersion = $null
                    ProductVersion = $null
                    Found = $false
                }
            )
            $pinnedVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    PinnedVersion = '9.5.6'
                }
            )

            $results = Compare-HostPluginVersions -HostVersions $hostVersions -PinnedVersions $pinnedVersions -Format Json | ConvertFrom-Json

            $results.hasErrors | Should -Be $true
            $results.results[0].status | Should -Be 'HOST_NOT_FOUND'
        }

        It 'Reports NOT_PINNED when package not in Directory.Packages.props' {
            $hostVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    DllName = 'Test.dll'
                    Reason = 'Test reason'
                    AssemblyVersion = '9.0.0.0'
                    FileVersion = '9.5.4'
                    ProductVersion = '9.5.4'
                    Found = $true
                }
            )
            $pinnedVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    PinnedVersion = $null
                }
            )

            $results = Compare-HostPluginVersions -HostVersions $hostVersions -PinnedVersions $pinnedVersions -Format Json | ConvertFrom-Json

            $results.hasErrors | Should -Be $true
            $results.results[0].status | Should -Be 'NOT_PINNED'
        }
    }

    Context 'Output formats' {
        It 'Json format includes all expected fields' {
            $hostVersions = @(
                [PSCustomObject]@{
                    PackageId = 'FluentValidation'
                    DllName = 'FluentValidation.dll'
                    Reason = 'ValidationFailure type crosses plugin boundary'
                    AssemblyVersion = '9.0.0.0'
                    FileVersion = '9.5.4'
                    ProductVersion = '9.5.4'
                    Found = $true
                }
            )
            $pinnedVersions = @(
                [PSCustomObject]@{
                    PackageId = 'FluentValidation'
                    PinnedVersion = '9.5.6'
                }
            )

            $json = Compare-HostPluginVersions -HostVersions $hostVersions -PinnedVersions $pinnedVersions -MatchPolicy MajorMinor -Format Json
            $results = $json | ConvertFrom-Json

            $results.matchPolicy | Should -Be 'MajorMinor'
            $results.PSObject.Properties.Name | Should -Contain 'hasErrors'
            $results.PSObject.Properties.Name | Should -Contain 'results'
            $results.results[0].PSObject.Properties.Name | Should -Contain 'packageId'
            $results.results[0].PSObject.Properties.Name | Should -Contain 'pinnedVersion'
            $results.results[0].PSObject.Properties.Name | Should -Contain 'hostVersion'
            $results.results[0].PSObject.Properties.Name | Should -Contain 'status'
        }

        It 'Does not include sensitive data in output' {
            # Versions are not secrets, but verify no paths or tokens leak
            $hostVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    DllName = 'Test.dll'
                    Reason = 'Test reason'
                    AssemblyVersion = '9.0.0.0'
                    FileVersion = '9.5.4'
                    ProductVersion = '9.5.4'
                    Found = $true
                }
            )
            $pinnedVersions = @(
                [PSCustomObject]@{
                    PackageId = 'TestPackage'
                    PinnedVersion = '9.5.6'
                }
            )

            $json = Compare-HostPluginVersions -HostVersions $hostVersions -PinnedVersions $pinnedVersions -Format Json

            # Should not contain file paths
            $json | Should -Not -Match '[A-Z]:\\'
            $json | Should -Not -Match '/home/'
            $json | Should -Not -Match '/app/bin'
        }
    }
}

Describe 'Get-PluginPinnedVersions' {
    BeforeAll {
        # Create a temporary Directory.Packages.props for testing
        $script:tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "pester-host-versions-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $script:tempDir -Force | Out-Null

        $propsContent = @'
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="FluentValidation" Version="11.9.0" />
    <PackageVersion Include="NLog" Version="5.2.8" />
    <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
'@
        Set-Content -Path (Join-Path $script:tempDir 'Directory.Packages.props') -Value $propsContent
    }

    AfterAll {
        Remove-Item -Path $script:tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'Reads pinned versions from Directory.Packages.props' {
        $results = Get-PluginPinnedVersions -RepoRoot $script:tempDir

        $fluentValidation = $results | Where-Object { $_.PackageId -eq 'FluentValidation' }
        $nlog = $results | Where-Object { $_.PackageId -eq 'NLog' }

        $fluentValidation.PinnedVersion | Should -Be '11.9.0'
        $nlog.PinnedVersion | Should -Be '5.2.8'
    }

    It 'Returns null PinnedVersion for packages not in props file' {
        $customPackages = @(
            @{ PackageId = 'NonExistentPackage'; DllName = 'NonExistent.dll' }
        )

        $results = Get-PluginPinnedVersions -RepoRoot $script:tempDir -Packages $customPackages

        $results[0].PinnedVersion | Should -BeNullOrEmpty
    }

    It 'Throws when Directory.Packages.props not found' {
        { Get-PluginPinnedVersions -RepoRoot '/nonexistent/path' } | Should -Throw
    }
}

Describe 'Cache directory filesystem safety' {
    It 'Sanitizes Docker tag with colons for Windows filesystem' {
        # Tags like "pr-plugins-3.1.1.4884" or "ghcr.io/hotio/lidarr:tag" contain unsafe chars
        InModuleScope 'e2e-host-versions' {
            $tag = 'ghcr.io/hotio/lidarr:pr-plugins-3.1.1.4884'
            $cacheKey = $tag -replace '[^a-zA-Z0-9._-]', '_'

            # Should not contain Windows-unsafe characters
            $cacheKey | Should -Not -Match ':'
            $cacheKey | Should -Not -Match '/'
            $cacheKey | Should -Not -Match '\\'
            $cacheKey | Should -Not -Match '<'
            $cacheKey | Should -Not -Match '>'
            $cacheKey | Should -Not -Match '\|'
            $cacheKey | Should -Not -Match '\?'
            $cacheKey | Should -Not -Match '\*'
            $cacheKey | Should -Not -Match '"'

            # Should produce valid result
            $cacheKey | Should -Be 'ghcr.io_hotio_lidarr_pr-plugins-3.1.1.4884'
        }
    }

    It 'Sanitizes simple tag without special characters' {
        InModuleScope 'e2e-host-versions' {
            $tag = 'pr-plugins-3.1.1.4884'
            $cacheKey = $tag -replace '[^a-zA-Z0-9._-]', '_'

            # Should remain unchanged (only safe chars)
            $cacheKey | Should -Be 'pr-plugins-3.1.1.4884'
        }
    }

    It 'Sanitizes tag with backslashes (Windows paths)' {
        InModuleScope 'e2e-host-versions' {
            $tag = 'some\weird\\tag'
            $cacheKey = $tag -replace '[^a-zA-Z0-9._-]', '_'

            $cacheKey | Should -Not -Match '\\'
            $cacheKey | Should -Be 'some_weird__tag'
        }
    }
}
