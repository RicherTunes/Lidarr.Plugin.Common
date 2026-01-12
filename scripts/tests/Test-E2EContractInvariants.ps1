#Requires -Modules Pester

<#
.SYNOPSIS
    E2E run-manifest global invariant tests.

.DESCRIPTION
    Fixture-driven tests that enforce critical contracts across all golden manifests:
    - No failed result without errorCode
    - errorCode format: ^E2E_[A-Z0-9_]+$
    - Cap invariants (count >= array.Length, capped flag consistency)
    - Schema validation (via child process to avoid exit killing Pester)
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Describe 'E2E run-manifest global invariants (golden fixtures)' {

    BeforeDiscovery {
        $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $script:FixturesDir = Join-Path $PSScriptRoot 'fixtures/golden-manifests'
        $script:Fixtures = Get-ChildItem -Path $script:FixturesDir -Filter '*.json' | Sort-Object Name
        $script:SchemaPath = Join-Path $script:RepoRoot 'docs/reference/e2e-run-manifest.schema.json'
        $script:ValidatorPath = Join-Path $script:RepoRoot 'scripts/validate-manifest.ps1'
    }

    BeforeAll {
        $script:RepoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $script:FixturesDir = Join-Path $PSScriptRoot 'fixtures/golden-manifests'
        $script:SchemaPath = Join-Path $script:RepoRoot 'docs/reference/e2e-run-manifest.schema.json'
        $script:ValidatorPath = Join-Path $script:RepoRoot 'scripts/validate-manifest.ps1'

        # Helper function to check if object has specified properties
        function script:Test-HasProps {
            param(
                [Parameter(Mandatory)] $Object,
                [Parameter(Mandatory)] [string[]] $Names
            )
            $propNames = @($Object.PSObject.Properties.Name)
            foreach ($n in $Names) {
                if ($propNames -notcontains $n) { return $false }
            }
            return $true
        }

        # Helper function to assert capped array invariants
        function script:Assert-CappedArrayInvariant {
            param(
                [Parameter(Mandatory)] $Details,
                [Parameter(Mandatory)] [string] $ArrayName,
                [Parameter(Mandatory)] [string] $CountName,
                [Parameter(Mandatory)] [string] $CappedName
            )

            if (-not (Test-HasProps $Details @($ArrayName, $CountName, $CappedName))) { return }

            $arr = @($Details.$ArrayName)
            $count = [int]$Details.$CountName
            $capped = [bool]$Details.$CappedName

            # count must be >= array length (array is capped subset)
            $count | Should -BeGreaterOrEqual $arr.Count

            # capped flag must reflect truncation truth
            ($capped -eq ($count -gt $arr.Count)) | Should -BeTrue

            # if capped, array must be non-empty (otherwise it's meaningless)
            if ($capped) {
                $arr.Count | Should -BeGreaterThan 0
            }
        }
    }

    It 'Has at least one fixture to test' {
        $fixtures = Get-ChildItem -Path $script:FixturesDir -Filter '*.json'
        $fixtures.Count | Should -BeGreaterThan 0
    }

    Context 'Schema validation' {
        It '<_.Name> is schema-valid' -ForEach $script:Fixtures {
            if (-not (Test-Path $script:ValidatorPath)) {
                Set-ItResult -Skipped -Because "validate-manifest.ps1 missing at $script:ValidatorPath"
                return
            }
            if (-not (Test-Path $script:SchemaPath)) {
                Set-ItResult -Skipped -Because "schema missing at $script:SchemaPath"
                return
            }

            # IMPORTANT: run validator in a child pwsh process so `exit` doesn't terminate Pester.
            $null = & pwsh -NoProfile -File $script:ValidatorPath `
                -ManifestPath $_.FullName `
                -SchemaPath $script:SchemaPath `
                -Quiet 2>&1

            $code = $LASTEXITCODE

            if ($code -eq 2) {
                Set-ItResult -Skipped -Because "No schema validator available in this environment (validate-manifest exit 2)"
                return
            }

            $code | Should -Be 0
        }
    }

    Context 'ErrorCode and result invariants' {
        It '<_.Name> obeys errorCode/result invariants' -ForEach $script:Fixtures {
            $manifest = Get-Content -Raw -Path $_.FullName | ConvertFrom-Json -Depth 100

            $manifest | Should -Not -BeNullOrEmpty
            $manifest.schemaVersion | Should -Not -BeNullOrEmpty
            @($manifest.results).Count | Should -BeGreaterThan 0

            foreach ($r in @($manifest.results)) {
                $r.gate | Should -Not -BeNullOrEmpty
                $r.plugin | Should -Not -BeNullOrEmpty
                $r.outcome | Should -BeIn @('success', 'failed', 'skipped')

                # If details.errorCode exists, it must agree with top-level errorCode (no drift).
                if ($null -ne $r.details -and (Test-HasProps $r.details @('errorCode'))) {
                    $r.details.errorCode | Should -Be $r.errorCode
                }

                if ($r.outcome -eq 'failed') {
                    # CRITICAL: Failed results MUST have errorCode
                    $r.errorCode | Should -Not -BeNullOrEmpty
                    $r.errorCode | Should -Match '^E2E_[A-Z0-9_]+$'
                    $r.details | Should -Not -BeNullOrEmpty
                }
                else {
                    # For success/skipped, errorCode may be null/empty OR present (but if present must be valid format)
                    if (-not [string]::IsNullOrWhiteSpace([string]$r.errorCode)) {
                        $r.errorCode | Should -Match '^E2E_[A-Z0-9_]+$'
                    }
                }

                if ($null -ne $r.details) {
                    # Cap invariants (only enforced when the triplet exists)
                    Assert-CappedArrayInvariant $r.details 'foundIndexerNames' 'foundIndexerNameCount' 'foundIndexerNamesCapped'
                    Assert-CappedArrayInvariant $r.details 'missingTags' 'missingTagsCount' 'missingTagsCapped'
                    Assert-CappedArrayInvariant $r.details 'validationErrors' 'validationErrorsCount' 'validationErrorsCapped'
                    Assert-CappedArrayInvariant $r.details 'nullIndexerSamples' 'nullIndexerReleaseCount' 'nullIndexerSamplesCapped'
                }
            }

            # Optional global sanity: if summary exists, it must be consistent.
            if ($null -ne $manifest.summary) {
                $passed = [int]($manifest.summary.passed ?? 0)
                $failed = [int]($manifest.summary.failed ?? 0)
                $skipped = [int]($manifest.summary.skipped ?? 0)
                $total = [int]($manifest.summary.totalGates ?? ($passed + $failed + $skipped))
                ($passed + $failed + $skipped) | Should -Be $total
                ($manifest.summary.overallSuccess -eq ($failed -eq 0)) | Should -BeTrue
            }
        }
    }
}
