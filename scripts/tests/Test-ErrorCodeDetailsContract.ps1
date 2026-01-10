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

    It 'E2E_METADATA_MISSING: missingTags is non-empty and capped at 10' {
        $repoRoot = Get-RepoRoot
        $goldenDir = Join-Path $repoRoot 'scripts/tests/fixtures/golden-manifests'

        $metadataMissingFixtures = @(Get-GoldenErrorResults -GoldenDir $goldenDir | Where-Object { $_.ErrorCode -eq 'E2E_METADATA_MISSING' })

        if ($metadataMissingFixtures.Count -eq 0) {
            throw "No E2E_METADATA_MISSING fixtures found - test would vacuously pass"
        }

        $failures = @()
        foreach ($item in $metadataMissingFixtures) {
            $details = $item.Details

            # missingTags must be non-empty
            if (-not $details.missingTags -or $details.missingTags.Count -eq 0) {
                $failures += "Fixture '$($item.File)': missingTags is empty or missing"
            }

            # missingTags must be capped at 10
            if ($details.missingTags -and $details.missingTags.Count -gt 10) {
                $failures += "Fixture '$($item.File)': missingTags exceeds cap of 10 (got $($details.missingTags.Count))"
            }

            # If capped, missingTagsCount should be >= missingTags.Count
            if ($details.missingTagsCapped -eq $true) {
                if ($details.missingTagsCount -lt $details.missingTags.Count) {
                    $failures += "Fixture '$($item.File)': missingTagsCapped=true but missingTagsCount < missingTags.Count"
                }
            }
        }

        if ($failures.Count -gt 0) {
            throw ("E2E_METADATA_MISSING contract violations:`n- " + ($failures -join "`n- "))
        }
    }

    It 'E2E_METADATA_MISSING: tagReadTool is always present and valid enum' {
        $repoRoot = Get-RepoRoot
        $goldenDir = Join-Path $repoRoot 'scripts/tests/fixtures/golden-manifests'

        $metadataMissingFixtures = @(Get-GoldenErrorResults -GoldenDir $goldenDir | Where-Object { $_.ErrorCode -eq 'E2E_METADATA_MISSING' })

        if ($metadataMissingFixtures.Count -eq 0) {
            throw "No E2E_METADATA_MISSING fixtures found - test would vacuously pass"
        }

        $validTagReadTools = @('mutagen', 'taglib', 'unknown')
        $failures = @()

        foreach ($item in $metadataMissingFixtures) {
            $details = $item.Details

            # tagReadTool must be present
            if ([string]::IsNullOrWhiteSpace($details.tagReadTool)) {
                $failures += "Fixture '$($item.File)': tagReadTool is missing or empty"
                continue
            }

            # tagReadTool must be a valid enum value
            if (-not ($validTagReadTools -contains $details.tagReadTool)) {
                $failures += "Fixture '$($item.File)': tagReadTool '$($details.tagReadTool)' is not a valid enum (expected: $($validTagReadTools -join ', '))"
            }
        }

        if ($failures.Count -gt 0) {
            throw ("E2E_METADATA_MISSING tagReadTool violations:`n- " + ($failures -join "`n- "))
        }
    }

    It 'E2E_METADATA_MISSING: tagReadToolVersion is null or short string' {
        $repoRoot = Get-RepoRoot
        $goldenDir = Join-Path $repoRoot 'scripts/tests/fixtures/golden-manifests'

        $metadataMissingFixtures = @(Get-GoldenErrorResults -GoldenDir $goldenDir | Where-Object { $_.ErrorCode -eq 'E2E_METADATA_MISSING' })

        if ($metadataMissingFixtures.Count -eq 0) {
            throw "No E2E_METADATA_MISSING fixtures found - test would vacuously pass"
        }

        $maxVersionLength = 64  # Reasonable cap for version strings
        $failures = @()

        foreach ($item in $metadataMissingFixtures) {
            $details = $item.Details

            # tagReadToolVersion can be null (optional)
            if ($null -eq $details.tagReadToolVersion) {
                continue
            }

            # If present, must be a string and not too long
            if ($details.tagReadToolVersion -isnot [string]) {
                $failures += "Fixture '$($item.File)': tagReadToolVersion is not a string"
                continue
            }

            if ($details.tagReadToolVersion.Length -gt $maxVersionLength) {
                $failures += "Fixture '$($item.File)': tagReadToolVersion exceeds max length of $maxVersionLength (got $($details.tagReadToolVersion.Length))"
            }
        }

        if ($failures.Count -gt 0) {
            throw ("E2E_METADATA_MISSING tagReadToolVersion violations:`n- " + ($failures -join "`n- "))
        }
    }

    It 'E2E_METADATA_MISSING: sampleFile is non-empty string (basename only)' {
        $repoRoot = Get-RepoRoot
        $goldenDir = Join-Path $repoRoot 'scripts/tests/fixtures/golden-manifests'

        $metadataMissingFixtures = @(Get-GoldenErrorResults -GoldenDir $goldenDir | Where-Object { $_.ErrorCode -eq 'E2E_METADATA_MISSING' })

        if ($metadataMissingFixtures.Count -eq 0) {
            throw "No E2E_METADATA_MISSING fixtures found - test would vacuously pass"
        }

        $failures = @()

        foreach ($item in $metadataMissingFixtures) {
            $details = $item.Details

            # sampleFile must be non-empty
            if ([string]::IsNullOrWhiteSpace($details.sampleFile)) {
                $failures += "Fixture '$($item.File)': sampleFile is missing or empty"
                continue
            }

            # sampleFile should be basename only (no path separators)
            if ($details.sampleFile -match '[/\\]') {
                $failures += "Fixture '$($item.File)': sampleFile '$($details.sampleFile)' contains path separators (should be basename only)"
            }
        }

        if ($failures.Count -gt 0) {
            throw ("E2E_METADATA_MISSING sampleFile violations:`n- " + ($failures -join "`n- "))
        }
    }
}
