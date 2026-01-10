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

Describe 'E2E error-code details contract' {
    AfterAll {
        # Emit marker file ONLY after tests complete successfully
        # This is the "proof of execution" for workflow verification
        $markerDir = $env:RUNNER_TEMP
        if (-not $markerDir) { $markerDir = $env:TEMP }
        if (-not $markerDir) { $markerDir = '/tmp' }

        $markerPath = Join-Path $markerDir 'tripwire-contract-test.marker'
        $timestamp = [DateTime]::UtcNow.ToString('o')
        @{
            test = 'Test-ErrorCodeDetailsContract.ps1'
            executedAt = $timestamp
            status = 'completed'
        } | ConvertTo-Json | Set-Content -Path $markerPath -Encoding UTF8

        Write-Host "TRIPWIRE_MARKER_PATH=$markerPath" -ForegroundColor Green
        # Also emit to GITHUB_OUTPUT if available
        if ($env:GITHUB_OUTPUT) {
            "tripwire_marker=$markerPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8NoBOM -Append
        }
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

        foreach ($item in Get-GoldenErrorResults -GoldenDir $goldenDir) {
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

        if ($failures.Count -gt 0) {
            throw ("Error-code details contract violations:`n- " + ($failures -join "`n- "))
        }
    }
}
