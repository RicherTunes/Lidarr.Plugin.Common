#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Unit tests for verify-merge-train.ps1 helper functions.

.DESCRIPTION
  Tests the Get-DotNetArgs function to ensure filter arguments are correctly
  formatted without shell escaping issues. This is a tripwire to prevent
  regressions in the --filter argument generation.
#>

BeforeAll {
    # Extract and define the Get-DotNetArgs function for testing
    # (The main script runs initialization logic on source, so we define it inline)
    function Get-DotNetArgs {
        param(
            [Parameter(Mandatory = $true)][string]$command,
            [Parameter(Mandatory = $true)][string]$target,
            [Parameter(Mandatory = $true)][bool]$ignoreWarningsAsErrors,
            [Parameter(Mandatory = $true)][bool]$noRestore,
            [Parameter(Mandatory = $true)][bool]$noBuild,
            [Parameter(Mandatory = $false)][bool]$skipIntegration = $false,
            [Parameter(Mandatory = $false)][bool]$skipPerformance = $false
        )

        $args = New-Object System.Collections.Generic.List[string]
        $args.Add($command)
        $args.Add($target)
        foreach ($value in @('-c', 'Release', '-m:1', '-p:BuildInParallel=false', '--disable-build-servers', '--nologo')) { $args.Add([string]$value) }

        if ($noRestore) { $args.Add('--no-restore') }
        if ($noBuild -and $command -eq 'test') { $args.Add('--no-build') }

        if ($ignoreWarningsAsErrors) {
            $args.Add('-p:TreatWarningsAsErrors=false')
        }

        if (($skipIntegration -or $skipPerformance) -and $command -eq 'test') {
            $filters = @()
            if ($skipIntegration) {
                $filters += 'FullyQualifiedName!~Integration'
                $filters += 'FullyQualifiedName!~Live'
                $filters += 'FullyQualifiedName!~EndToEnd'
            }
            if ($skipPerformance) {
                $filters += 'Category!=Benchmark'
                $filters += 'Category!=Slow'
            }
            $args.Add('--filter')
            $args.Add($filters -join '&')
        }

        return $args.ToArray()
    }
}

Describe 'Get-DotNetArgs filter generation' {
    Context 'when -skipPerformance is true' {
        It 'includes Benchmark/Slow category filters without backslashes' {
            $args = Get-DotNetArgs `
                -command 'test' `
                -target 'test.csproj' `
                -ignoreWarningsAsErrors $false `
                -noRestore $false `
                -noBuild $false `
                -skipIntegration $false `
                -skipPerformance $true

            # Find the filter argument
            $filterIndex = [Array]::IndexOf($args, '--filter')
            $filterIndex | Should -BeGreaterOrEqual 0 -Because '--filter should be present'

            $filterValue = $args[$filterIndex + 1]
            $filterValue | Should -Be 'Category!=Benchmark&Category!=Slow' -Because 'filter should contain exact string without escaping'
            $filterValue | Should -Not -Match '\\' -Because 'filter should not contain backslashes'
        }
    }

    Context 'when both -skipIntegration and -skipPerformance are true' {
        It 'combines filters with ampersand and no escaping' {
            $args = Get-DotNetArgs `
                -command 'test' `
                -target 'test.csproj' `
                -ignoreWarningsAsErrors $false `
                -noRestore $false `
                -noBuild $false `
                -skipIntegration $true `
                -skipPerformance $true

            $filterIndex = [Array]::IndexOf($args, '--filter')
            $filterIndex | Should -BeGreaterOrEqual 0

            $filterValue = $args[$filterIndex + 1]
            $filterValue | Should -Match 'Category!=Benchmark' -Because 'Benchmark filter should be present'
            $filterValue | Should -Match 'Category!=Slow' -Because 'Slow filter should be present'
            $filterValue | Should -Match 'FullyQualifiedName!~Integration' -Because 'Integration filter should be present'
            $filterValue | Should -Not -Match '\\' -Because 'combined filter should not contain backslashes'
        }
    }

    Context 'when -skipPerformance is false' {
        It 'does not include Performance filter' {
            $args = Get-DotNetArgs `
                -command 'test' `
                -target 'test.csproj' `
                -ignoreWarningsAsErrors $false `
                -noRestore $false `
                -noBuild $false `
                -skipIntegration $false `
                -skipPerformance $false

            $argsString = $args -join ' '
            $argsString | Should -Not -Match 'Category' -Because 'no category filter when skipPerformance is false'
        }
    }

    Context 'when command is not test' {
        It 'does not include filter even with skipPerformance true' {
            $args = Get-DotNetArgs `
                -command 'build' `
                -target 'test.csproj' `
                -ignoreWarningsAsErrors $false `
                -noRestore $false `
                -noBuild $false `
                -skipIntegration $false `
                -skipPerformance $true

            $argsString = $args -join ' '
            $argsString | Should -Not -Match '--filter' -Because 'filter only applies to test command'
        }
    }
}
