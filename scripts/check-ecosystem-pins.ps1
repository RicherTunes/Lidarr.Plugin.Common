#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Cross-repo drift guard: assert the streaming plugins all pin the SAME Lidarr.Plugin.Common SHA.

.DESCRIPTION
    The consolidation program requires a shared-behavior fix to land in ONE Common implementation and
    reach every plugin. That only holds if the plugins actually sit on the same Common pin. This script
    reads each plugin's `ext-common-sha.txt` from its default branch via the GitHub API and FAILS if the
    streaming plugins (qobuz/tidal/apple) disagree with each other.

    Layering note: this is the ECOSYSTEM-AGREEMENT layer (do all plugins pin the same Common?). The
    per-plugin reusable `verify-common-pins.yml` is the WITHIN-PLUGIN layer (does a plugin's gitlink
    match its own ext-common-sha.txt?). They are complementary.

    Graceful degradation: a repo the token can't read (e.g. private applemusicarr under the default
    GITHUB_TOKEN) is reported as "unreadable" and EXCLUDED from the agreement set rather than failing the
    run — matching how `ecosystem-notify.yml` already omits the private repo. Provide a PAT with repo-read
    scope via $env:GH_TOKEN to include it.

    Advisory repos (brainarr) are reported for visibility but never fail the run: brainarr is an
    import-list plugin whose Common consolidation is intentionally deferred, so it is allowed to lag.

.PARAMETER StreamingRepos
    owner/repo of the streaming plugins that MUST agree. Default: the three streaming plugins.

.PARAMETER AdvisoryRepos
    owner/repo of plugins reported-but-not-enforced. Default: brainarr.

.PARAMETER CommonRepo
    owner/repo of Common, used only to report how far each plugin lags behind Common's default branch.

.EXAMPLE
    pwsh scripts/check-ecosystem-pins.ps1
.EXAMPLE
    $env:GH_TOKEN = '<repo-read PAT>'; pwsh scripts/check-ecosystem-pins.ps1   # includes private apple
#>
[CmdletBinding()]
param(
    [string[]] $StreamingRepos = @('RicherTunes/Qobuzarr', 'RicherTunes/Tidalarr', 'RicherTunes/AppleMusicarr'),
    [string[]] $AdvisoryRepos  = @('RicherTunes/Brainarr'),
    [string]   $CommonRepo     = 'RicherTunes/Lidarr.Plugin.Common'
)

$ErrorActionPreference = 'Stop'

function Get-PinnedCommonSha {
    <# Returns the trimmed ext-common-sha.txt of a repo's default branch, or $null if unreadable. #>
    param([string] $Repo)
    try {
        $b64 = gh api "repos/$Repo/contents/ext-common-sha.txt" --jq '.content' 2>$null
        if (-not $b64) { return $null }
        $raw = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String(($b64 -replace '\s', '')))
        $sha = $raw.Trim()
        if ($sha -match '^[0-9a-fA-F]{40}$') { return $sha.ToLowerInvariant() }
        return $null
    }
    catch { return $null }
}

function Test-EcosystemPinAgreement {
    <#
      Pure decision function (no I/O) so it is unit-testable. Given a map of streaming repo -> sha
      (sha may be $null when unreadable), returns @{ Drifted = <bool>; Pins = <distinct readable shas> }.
      Drift = two or more DISTINCT readable shas among the streaming repos.
    #>
    param([hashtable] $StreamingPins)
    $readable = @($StreamingPins.Values | Where-Object { $_ })
    $distinct = @($readable | Select-Object -Unique)
    return @{ Drifted = ($distinct.Count -gt 1); Pins = $distinct }
}

# --- Gather pins ---------------------------------------------------------------------------------
$commonHead = $null
try { $commonHead = (gh api "repos/$CommonRepo/commits/HEAD" --jq '.sha' 2>$null)?.Trim().ToLowerInvariant() } catch {}

$streamingPins = @{}
foreach ($r in $StreamingRepos) { $streamingPins[$r] = Get-PinnedCommonSha -Repo $r }
$advisoryPins = @{}
foreach ($r in $AdvisoryRepos) { $advisoryPins[$r] = Get-PinnedCommonSha -Repo $r }

# --- Report --------------------------------------------------------------------------------------
Write-Host "Ecosystem Common-pin drift check"
if ($commonHead) { Write-Host "  Common default-branch HEAD: $($commonHead.Substring(0,12))" }
Write-Host ""
Write-Host "  Streaming plugins (must agree):"
foreach ($r in $StreamingRepos) {
    $sha = $streamingPins[$r]
    $shown = if ($sha) { $sha.Substring(0, 12) } else { '<unreadable — token lacks read access>' }
    $lag = if ($sha -and $commonHead -and $sha -ne $commonHead) { ' (behind Common HEAD)' } else { '' }
    Write-Host ("    {0,-32} {1}{2}" -f $r, $shown, $lag)
}
Write-Host ""
Write-Host "  Advisory plugins (reported, never fail — consolidation deferred):"
foreach ($r in $AdvisoryRepos) {
    $sha = $advisoryPins[$r]
    $shown = if ($sha) { $sha.Substring(0, 12) } else { '<unreadable>' }
    Write-Host ("    {0,-32} {1}" -f $r, $shown)
}
Write-Host ""

# --- Verdict -------------------------------------------------------------------------------------
$result = Test-EcosystemPinAgreement -StreamingPins $streamingPins
$unreadable = @($StreamingRepos | Where-Object { -not $streamingPins[$_] })
if ($unreadable.Count -gt 0) {
    Write-Host "::warning::Could not read Common pin for: $($unreadable -join ', '). Excluded from the agreement check (provide a repo-read PAT via GH_TOKEN to include private repos)."
}

if ($result.Drifted) {
    $detail = ($StreamingRepos | Where-Object { $streamingPins[$_] } | ForEach-Object { "$_=$($streamingPins[$_].Substring(0,12))" }) -join '; '
    Write-Host "::error::Streaming plugins pin DIFFERENT Common SHAs (drift): $detail"
    Write-Host "::error::A shared-behavior fix in Common will not reach all plugins until they re-pin to one SHA."
    exit 1
}

$agreed = if ($result.Pins.Count -eq 1) { $result.Pins[0].Substring(0, 12) } else { '(none readable)' }
Write-Host "OK: all readable streaming plugins agree on Common pin $agreed."
exit 0
