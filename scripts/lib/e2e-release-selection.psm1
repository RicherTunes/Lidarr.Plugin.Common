<#
.SYNOPSIS
    Deterministic release selection for E2E testing.

.DESCRIPTION
    Provides culture-invariant, stable sorting for release selection to ensure
    reproducible E2E test results across different environments and runs.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Select-DeterministicRelease {
    <#
    .SYNOPSIS
        Selects a release deterministically from an array of releases.

    .DESCRIPTION
        Uses culture-invariant sorting with multiple tie-breaker keys to ensure
        the same release is selected regardless of input order or system locale.

        Sort order:
        1. title (ascending, case-insensitive, culture-invariant)
        2. guid (ascending, case-insensitive, culture-invariant)
        3. size (descending, largest first)

    .PARAMETER Releases
        Array of release objects from Lidarr API.

    .PARAMETER ReturnSelectionBasis
        When set, returns a hashtable with 'release' and 'selectionBasis' keys
        for manifest output.

    .OUTPUTS
        Release object, or hashtable with release + selectionBasis if ReturnSelectionBasis is set.

    .EXAMPLE
        $release = Select-DeterministicRelease -Releases $releases

    .EXAMPLE
        $result = Select-DeterministicRelease -Releases $releases -ReturnSelectionBasis
        $release = $result.release
        $manifest.details.selectionBasis = $result.selectionBasis
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [array]$Releases,

        [switch]$ReturnSelectionBasis
    )

    if (-not $Releases -or $Releases.Count -eq 0) {
        if ($ReturnSelectionBasis) {
            return @{ release = $null; selectionBasis = @{ error = 'no_releases' } }
        }
        return $null
    }

    # Sort with culture-invariant keys and stable ordering
    $sorted = $Releases | Sort-Object -Stable `
        @{Expression = { ($_.title ?? '').ToUpperInvariant() }; Ascending = $true },
        @{Expression = { ($_.guid ?? '').ToUpperInvariant() }; Ascending = $true },
        @{Expression = { $_.size ?? 0 }; Descending = $true }

    $selected = $sorted | Select-Object -First 1

    if ($ReturnSelectionBasis) {
        # Build selection basis for manifest
        $basis = [ordered]@{
            sortKeys = @('title:asc:invariant', 'guid:asc:invariant', 'size:desc')
            candidateCount = $Releases.Count
            selectedTitle = $selected.title
            selectedGuid = $selected.guid
            selectedSize = $selected.size
        }

        # Check if there were ties on primary key
        $sameTitleCount = @($Releases | Where-Object {
            ($_.title ?? '').ToUpperInvariant() -eq ($selected.title ?? '').ToUpperInvariant()
        }).Count

        if ($sameTitleCount -gt 1) {
            $basis.titleTieCount = $sameTitleCount
            $basis.tieBreaker = 'guid'
        }

        return @{
            release = $selected
            selectionBasis = $basis
        }
    }

    return $selected
}

Export-ModuleMember -Function Select-DeterministicRelease
