#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    return (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
}

function Parse-StructuredDetailsContract {
    param(
        [Parameter(Mandatory)]
        [string]$DocPath
    )

    $content = Get-Content -Path $DocPath
    $inSection = $false
    $currentCode = $null
    $contracts = @{}

    foreach ($line in $content) {
        if (-not $inSection) {
            if ($line -match '^\s*##\s+Structured Details Contract\s*$') {
                $inSection = $true
            }
            continue
        }

        if ($line -match '^\s*##\s+') {
            break
        }

        $codeMatch = [regex]::Match($line, '^\s*###\s+`(?<code>E2E_[A-Z0-9_]+)`\s*$')
        if ($codeMatch.Success) {
            $currentCode = $codeMatch.Groups['code'].Value
            if (-not $contracts.ContainsKey($currentCode)) {
                $contracts[$currentCode] = @()
            }
            continue
        }

        if (-not $currentCode) {
            continue
        }

        # Table row: | `field` | type | notes |
        $rowMatch = [regex]::Match($line, '^\|\s*`(?<field>[^`]+)`\s*\|\s*(?<type>[^|]+?)\s*\|\s*(?<notes>[^|]*)\|\s*$')
        if (-not $rowMatch.Success) {
            continue
        }

        $field = $rowMatch.Groups['field'].Value.Trim()
        $type = $rowMatch.Groups['type'].Value.Trim()
        $notes = $rowMatch.Groups['notes'].Value.Trim()

        # Skip header rows.
        if ($field -eq 'Field') {
            continue
        }

        $isOptional = $type.Contains('?')

        $contracts[$currentCode] += [pscustomobject]@{
            Field = $field
            Type = $type
            Notes = $notes
            Optional = $isOptional
        }
    }

    return $contracts
}

function Get-GoldenErrorResults {
    param(
        [Parameter(Mandatory)]
        [string]$GoldenDir
    )

    $files = Get-ChildItem -Path $GoldenDir -Filter '*.json' | Sort-Object Name
    foreach ($file in $files) {
        $raw = Get-Content -Path $file.FullName -Raw
        $manifest = $raw | ConvertFrom-Json

        $errorResults = @($manifest.results | Where-Object { -not [string]::IsNullOrWhiteSpace($_.errorCode) })
        foreach ($result in $errorResults) {
            [pscustomobject]@{
                File = $file.Name
                ErrorCode = "$($result.errorCode)"
                Details = $result.details
            }
        }
    }
}

function Write-TripwireMarker {
    param(
        [int]$ContractsChecked,
        [int]$FixturesChecked
    )

    # Emit marker file as proof of successful execution
    # This is ONLY called after all assertions pass
    $markerDir = $env:RUNNER_TEMP
    if (-not $markerDir) { $markerDir = $env:TEMP }
    if (-not $markerDir) { $markerDir = '/tmp' }

    $markerPath = Join-Path $markerDir 'tripwire-contract-test.marker'
    $timestamp = [DateTime]::UtcNow.ToString('o')

    @{
        test = 'Test-ErrorCodeDetailsContract.ps1'
        executedAt = $timestamp
        status = 'passed'
        contractsChecked = $ContractsChecked
        fixturesChecked = $FixturesChecked
    } | ConvertTo-Json | Set-Content -Path $markerPath -Encoding UTF8

    Write-Host "TRIPWIRE_MARKER_PATH=$markerPath" -ForegroundColor Green
}

Describe 'E2E error-code details contract' {
    BeforeAll {
        $script:repoRoot = Get-RepoRoot
        $script:goldenDir = Join-Path $script:repoRoot 'scripts/tests/fixtures/golden-manifests'
    }

    It 'Structured details contract matches golden fixtures (required fields present)' {
        $repoRoot = Get-RepoRoot
        $docPath = Join-Path $repoRoot 'docs/E2E_ERROR_CODES.md'
        $goldenDir = Join-Path $repoRoot 'scripts/tests/fixtures/golden-manifests'

        if (-not (Test-Path $docPath)) {
            throw "docs/E2E_ERROR_CODES.md not found at: $docPath"
        }
        if (-not (Test-Path $goldenDir)) {
            throw "Golden manifests dir not found at: $goldenDir"
        }

        $contracts = Parse-StructuredDetailsContract -DocPath $docPath
        if ($contracts.Keys.Count -eq 0) {
            throw "No structured details contracts parsed from docs/E2E_ERROR_CODES.md (missing '## Structured Details Contract'?)"
        }

        $failures = @()
        $fixturesChecked = 0

        foreach ($item in Get-GoldenErrorResults -GoldenDir $goldenDir) {
            $fixturesChecked++
            $code = $item.ErrorCode
            $details = $item.Details

            if (-not $contracts.ContainsKey($code)) {
                $failures += "Fixture '$($item.File)' uses errorCode '$code' but docs has no contract section for it."
                continue
            }

            $requiredFields = @($contracts[$code] | Where-Object { -not $_.Optional } | Select-Object -ExpandProperty Field)

            if ($requiredFields.Count -eq 0) {
                continue
            }

            if ($null -eq $details) {
                $failures += "Fixture '$($item.File)' errorCode '$code' is missing details object."
                continue
            }

            $detailProps = @($details.PSObject.Properties.Name)

            foreach ($fieldName in $requiredFields) {
                if (-not ($detailProps -contains $fieldName)) {
                    $failures += "Fixture '$($item.File)' errorCode '$code' missing required details field '$fieldName' (per docs)."
                    continue
                }

                $value = $details.$fieldName
                if ($null -eq $value) {
                    $failures += "Fixture '$($item.File)' errorCode '$code' required details field '$fieldName' is null (per docs)."
                }
            }
        }

        # CRITICAL: Fail if no fixtures were checked (test would vacuously pass)
        if ($fixturesChecked -eq 0) {
            throw "No golden fixtures with errorCode found - test would vacuously pass"
        }

        if ($failures.Count -gt 0) {
            throw ("Error-code details contract violations:`n- " + ($failures -join "`n- "))
        }

        # SUCCESS: All assertions passed, emit proof marker
        # This line is ONLY reached if:
        #   1. Contracts were parsed (Keys.Count > 0)
        #   2. Fixtures were checked (fixturesChecked > 0)
        #   3. No failures (failures.Count == 0)
        Write-TripwireMarker -ContractsChecked $contracts.Keys.Count -FixturesChecked $fixturesChecked
    }

    Context 'E2E_METADATA_MISSING contract' {
        It 'tagReadTool is a valid enum value (mutagen|taglib|unknown)' {
            $validValues = @('mutagen', 'taglib', 'unknown')

            foreach ($item in Get-GoldenErrorResults -GoldenDir $script:goldenDir) {
                if ($item.ErrorCode -ne 'E2E_METADATA_MISSING') { continue }

                $tagReadTool = $item.Details.tagReadTool
                $tagReadTool | Should Not BeNullOrEmpty
                ($validValues -contains $tagReadTool) | Should Be $true
            }
        }

        It 'tagReadToolVersion can be null (optional field)' {
            # This test validates that the fixture has tagReadToolVersion field and it can be null
            $foundMetadataMissing = $false

            foreach ($item in Get-GoldenErrorResults -GoldenDir $script:goldenDir) {
                if ($item.ErrorCode -ne 'E2E_METADATA_MISSING') { continue }
                $foundMetadataMissing = $true

                # Field should exist (even if null)
                ($item.Details.PSObject.Properties.Name -contains 'tagReadToolVersion') | Should Be $true
            }

            $foundMetadataMissing | Should Be $true
        }

        It 'sampleFile contains no path separators (basename only)' {
            foreach ($item in Get-GoldenErrorResults -GoldenDir $script:goldenDir) {
                if ($item.ErrorCode -ne 'E2E_METADATA_MISSING') { continue }

                $sampleFile = $item.Details.sampleFile
                $sampleFile | Should Not BeNullOrEmpty
                $sampleFile | Should Not Match '[/\\]'
            }
        }

        It 'missingTags is non-empty array with max 10 entries' {
            foreach ($item in Get-GoldenErrorResults -GoldenDir $script:goldenDir) {
                if ($item.ErrorCode -ne 'E2E_METADATA_MISSING') { continue }

                $missingTags = @($item.Details.missingTags)
                ($missingTags.Count -gt 0) | Should Be $true
                ($missingTags.Count -le 10) | Should Be $true
            }
        }
    }
}
