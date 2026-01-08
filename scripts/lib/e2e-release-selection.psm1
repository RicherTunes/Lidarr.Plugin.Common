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

        Sort order (all keys canonicalized to handle null/empty/type variations):
        1. title (ascending, trimmed, ToUpperInvariant)
        2. guid (ascending, trimmed, ToLowerInvariant - GUIDs are case-insensitive)
        3. size (parsed as int64; descending by default, ascending if -SizeAscending)
        4. intrinsicHash (SHA256 of canonicalized properties - ensures input-order-independence)
        5. originalIndex (ascending, absolute last resort for hash collisions)

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

    # Helper to safely parse numeric values (handles string "123" vs int 123)
    function SafeParseInt64 {
        param($value, [int64]$default = 0)
        if ($null -eq $value) { return $default }
        $result = 0L
        if ([int64]::TryParse([string]$value, [ref]$result)) { return $result }
        return $default
    }
    function SafeParseInt {
        param($value, [int]$default = 0)
        if ($null -eq $value) { return $default }
        $result = 0
        if ([int]::TryParse([string]$value, [ref]$result)) { return $result }
        return $default
    }

    # Build indexed array with intrinsic hash as final tiebreaker
    # IntrinsicHash is based on release properties, not input order, ensuring
    # determinism even when input order is non-deterministic (e.g., from API)
    $sha = [System.Security.Cryptography.SHA256]::Create()

    $indexed = for ($i = 0; $i -lt $Releases.Count; $i++) {
        $r = $Releases[$i]

        # Canonicalize all hash inputs to prevent type/format nondeterminism:
        # - Trim whitespace, normalize case
        # - Parse numbers safely (handles "123" vs 123)
        # - Use ToLowerInvariant for guid (GUIDs are case-insensitive)
        $titleKey = if ($r.PSObject.Properties['title'] -and $r.title) {
            ([string]$r.title).Trim().ToUpperInvariant()
        } else { '' }

        $guidKey = if ($r.PSObject.Properties['guid'] -and $r.guid) {
            ([string]$r.guid).Trim().ToLowerInvariant()
        } else { '' }

        $rawSize = if ($r.PSObject.Properties['size']) { $r.size } else { $null }
        $sizeKey = SafeParseInt64 $rawSize $sizeDefault

        $rawIndexerId = if ($r.PSObject.Properties['indexerId']) { $r.indexerId } else { $null }
        $indexerIdKey = SafeParseInt $rawIndexerId 0

        # Build hash input from canonicalized primitives
        $hashInput = "$titleKey|$guidKey|$sizeKey|$indexerIdKey"

        # Add collision fallbacks for when primary fields are all defaulted
        # This ensures even releases with all-null fields can be differentiated
        if ($titleKey -eq '' -and $guidKey -eq '' -and $sizeKey -eq $sizeDefault -and $indexerIdKey -eq 0) {
            # Try downloadUrl hash (common differentiator)
            if ($r.PSObject.Properties['downloadUrl'] -and $r.downloadUrl) {
                $urlBytes = [System.Text.Encoding]::UTF8.GetBytes([string]$r.downloadUrl)
                $urlHash = [BitConverter]::ToString($sha.ComputeHash($urlBytes)).Replace('-', '').Substring(0, 16)
                $hashInput += "|url:$urlHash"
            }
            # Try infoUrl hash
            elseif ($r.PSObject.Properties['infoUrl'] -and $r.infoUrl) {
                $urlBytes = [System.Text.Encoding]::UTF8.GetBytes([string]$r.infoUrl)
                $urlHash = [BitConverter]::ToString($sha.ComputeHash($urlBytes)).Replace('-', '').Substring(0, 16)
                $hashInput += "|info:$urlHash"
            }
            # Try releaseId
            elseif ($r.PSObject.Properties['id'] -and $r.id) {
                $hashInput += "|id:$([string]$r.id)"
            }
            # Last resort: hash the entire object JSON
            else {
                $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes(($r | ConvertTo-Json -Compress -Depth 1))
                $jsonHash = [BitConverter]::ToString($sha.ComputeHash($jsonBytes)).Replace('-', '').Substring(0, 16)
                $hashInput += "|json:$jsonHash"
            }
        }

        # Compute intrinsic hash
        $hashBytes = [System.Text.Encoding]::UTF8.GetBytes($hashInput)
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
        $guidHash = if ($selected.PSObject.Properties['guid'] -and $selected.guid) {
            $guidBytes = [System.Text.Encoding]::UTF8.GetBytes([string]$selected.guid)
            $shaGuid = [System.Security.Cryptography.SHA256]::Create()
            $hashResult = $shaGuid.ComputeHash($guidBytes)
            [BitConverter]::ToString($hashResult).Replace('-', '').Substring(0, 16)
        } else { $null }

        # Safely access optional properties
        $selectedTitle = if ($selected.PSObject.Properties['title']) { $selected.title } else { $null }
        $selectedSize = if ($selected.PSObject.Properties['size']) { $selected.size } else { $null }

        $sizeDir = if ($SizeAscending) { 'asc' } else { 'desc' }
        $basis = [ordered]@{
            sortKeys = @('title:asc:invariant', 'guid:asc:invariant', "size:$sizeDir", 'intrinsicHash:asc', 'originalIndex:asc')
            candidateCount = $Releases.Count
            selectedTitle = $selectedTitle
            selectedGuidHash = $guidHash
            selectedSize = $selectedSize
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
