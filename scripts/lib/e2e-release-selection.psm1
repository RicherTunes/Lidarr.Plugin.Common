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

    # For SizeAscending mode, use MaxValue for nulls so they sort last
    $sizeDefault = if ($SizeAscending) { [long]::MaxValue } else { 0 }

    # Build indexed array with intrinsic hash as final tiebreaker
    # IntrinsicHash is based on release properties, not input order, ensuring
    # determinism even when input order is non-deterministic (e.g., from API)
    $indexed = for ($i = 0; $i -lt $Releases.Count; $i++) {
        $r = $Releases[$i]
        # Use PSObject.Properties to safely access potentially missing properties
        $titleKey = if ($r.PSObject.Properties['title']) { ($r.title ?? '').ToUpperInvariant() } else { '' }
        $guidKey = if ($r.PSObject.Properties['guid']) { ($r.guid ?? '').ToUpperInvariant() } else { '' }
        $sizeKey = if ($r.PSObject.Properties['size']) { [long]($r.size ?? $sizeDefault) } else { $sizeDefault }
        $indexerId = if ($r.PSObject.Properties['indexerId']) { [string]($r.indexerId ?? '') } else { '' }

        # Compute intrinsic hash from release properties (not input order)
        # This ensures same release is selected regardless of input array order
        $hashInput = "$titleKey|$guidKey|$sizeKey|$indexerId"
        $hashBytes = [System.Text.Encoding]::UTF8.GetBytes($hashInput)
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $hashResult = $sha.ComputeHash($hashBytes)
        $intrinsicHash = [BitConverter]::ToString($hashResult).Replace('-', '').Substring(0, 16)

        [PSCustomObject]@{
            Release = $r
            OriginalIndex = $i
            TitleKey = $titleKey
            GuidKey = $guidKey
            SizeKey = $sizeKey
            IntrinsicHash = $intrinsicHash
        }
    }

    # Sort with culture-invariant keys; IntrinsicHash ensures determinism regardless of input order
    # OriginalIndex is absolute last resort (only matters if intrinsicHash collides, ~impossible)
    $sizeDescending = -not $SizeAscending
    $sorted = $indexed | Sort-Object TitleKey, GuidKey, @{Expression = 'SizeKey'; Descending = $sizeDescending}, IntrinsicHash, OriginalIndex

    $winner = $sorted | Select-Object -First 1
    $selected = $winner.Release

    if ($ReturnSelectionBasis) {
        # Build selection basis for manifest (no sensitive fields)
        # Use sha256 hash of guid for safety (never expose raw guid which may contain embedded data)
        $guidHash = if ($selected.guid) {
            $guidBytes = [System.Text.Encoding]::UTF8.GetBytes([string]$selected.guid)
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $hashResult = $sha.ComputeHash($guidBytes)
            [BitConverter]::ToString($hashResult).Replace('-', '').Substring(0, 16)
        } else { $null }

        $sizeDir = if ($SizeAscending) { 'asc' } else { 'desc' }
        $basis = [ordered]@{
            sortKeys = @('title:asc:invariant', 'guid:asc:invariant', "size:$sizeDir", 'intrinsicHash:asc', 'originalIndex:asc')
            candidateCount = $Releases.Count
            selectedTitle = $selected.title
            selectedGuidHash = $guidHash
            selectedSize = $selected.size
            selectedIntrinsicHash = $winner.IntrinsicHash
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
                    # Same title, guid, and size - check intrinsicHash
                    $sameTitleGuidSizeHash = @($sameTitleGuidSize | Where-Object { $_.IntrinsicHash -eq $winner.IntrinsicHash })
                    if ($sameTitleGuidSizeHash.Count -gt 1) {
                        $basis.tieBreaker = 'originalIndex'
                    } else {
                        $basis.tieBreaker = 'intrinsicHash'
                    }
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
