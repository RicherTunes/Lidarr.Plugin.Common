<#
.SYNOPSIS
    Deterministic release selection for E2E testing.

.DESCRIPTION
    Provides culture-invariant sorting for release selection to ensure
    reproducible E2E test results across different environments and runs.

    Works on both PowerShell 5.1 (Windows PowerShell) and PowerShell 7+ (pwsh)
    by using originalIndex as final tiebreaker instead of relying on -Stable.
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

        Does NOT rely on Sort-Object -Stable (pwsh 7+ only). Instead, uses
        originalIndex as final tiebreaker to guarantee stability on all
        PowerShell versions.

        Sort order (all keys normalized to handle null/empty):
        1. title (ascending, case-insensitive via ToUpperInvariant)
        2. guid (ascending, case-insensitive via ToUpperInvariant)
        3. size (default: descending/largest first; or ascending if -SizeAscending)
        4. originalIndex (ascending, ensures stability)

        "Tie" detection uses normalized keys, so titles differing only by
        case/whitespace are considered equal for tie-counting purposes.

    .PARAMETER Releases
        Array of release objects from Lidarr API.

    .PARAMETER SizeAscending
        When set, sorts by size ascending (smallest first). Useful for smoke tests
        where smaller releases download faster. Default is size descending.

    .PARAMETER ReturnSelectionBasis
        When set, returns a hashtable with 'release' and 'selectionBasis' keys
        for manifest output. SelectionBasis never includes sensitive fields
        (URLs, tokens, etc.) - only title, guid (truncated), size, and counts.

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

        [switch]$SizeAscending,

        [switch]$ReturnSelectionBasis
    )

    if (-not $Releases -or $Releases.Count -eq 0) {
        if ($ReturnSelectionBasis) {
            return @{ release = $null; selectionBasis = @{ error = 'no_releases' } }
        }
        return $null
    }

    # Add originalIndex for stable sorting without -Stable flag (works on PS 5.1+)
    # For SizeAscending mode, use MaxValue for nulls so they sort last
    $sizeDefault = if ($SizeAscending) { [long]::MaxValue } else { 0 }

    $indexed = for ($i = 0; $i -lt $Releases.Count; $i++) {
        [PSCustomObject]@{
            Release = $Releases[$i]
            OriginalIndex = $i
            # Pre-compute normalized keys for consistent comparison
            TitleKey = ($Releases[$i].title ?? '').ToUpperInvariant()
            GuidKey = ($Releases[$i].guid ?? '').ToUpperInvariant()
            SizeKey = [long]($Releases[$i].size ?? $sizeDefault)
        }
    }

    # Sort with culture-invariant keys; originalIndex guarantees stability
    $sizeDescending = -not $SizeAscending
    $sorted = $indexed | Sort-Object TitleKey, GuidKey, @{Expression = 'SizeKey'; Descending = $sizeDescending}, OriginalIndex

    $winner = $sorted | Select-Object -First 1
    $selected = $winner.Release

    if ($ReturnSelectionBasis) {
        # Build selection basis for manifest (no sensitive fields)
        # Truncate guid to first 16 chars for safety (in case it contains embedded data)
        $safeGuid = if ($selected.guid) {
            $g = [string]$selected.guid
            if ($g.Length -gt 16) { $g.Substring(0, 16) + '...' } else { $g }
        } else { $null }

        $sizeDir = if ($SizeAscending) { 'asc' } else { 'desc' }
        $basis = [ordered]@{
            sortKeys = @('title:asc:invariant', 'guid:asc:invariant', "size:$sizeDir", 'originalIndex:asc')
            candidateCount = $Releases.Count
            selectedTitle = $selected.title
            selectedGuidPrefix = $safeGuid
            selectedSize = $selected.size
            selectedOriginalIndex = $winner.OriginalIndex
        }

        # Check if there were ties on primary key (after normalization)
        $sameTitleCount = @($indexed | Where-Object { $_.TitleKey -eq $winner.TitleKey }).Count
        if ($sameTitleCount -gt 1) {
            $basis.titleTieCount = $sameTitleCount

            # Determine which key actually broke the tie
            $sameTitleAndGuid = @($indexed | Where-Object {
                $_.TitleKey -eq $winner.TitleKey -and $_.GuidKey -eq $winner.GuidKey
            })

            if ($sameTitleAndGuid.Count -gt 1) {
                $sameTitleGuidSize = @($sameTitleAndGuid | Where-Object { $_.SizeKey -eq $winner.SizeKey })
                if ($sameTitleGuidSize.Count -gt 1) {
                    $basis.tieBreaker = 'originalIndex'
                } else {
                    $basis.tieBreaker = 'size'
                }
            } else {
                $basis.tieBreaker = 'guid'
            }
        }

        return @{
            release = $selected
            selectionBasis = $basis
        }
    }

    return $selected
}

Export-ModuleMember -Function Select-DeterministicRelease
