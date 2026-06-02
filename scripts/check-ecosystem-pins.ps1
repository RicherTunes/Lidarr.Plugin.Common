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

    Architectural limit (why this is a scheduled/dispatch guard, not a PR gate): a plugin's OWN pull
    request cannot observe sibling-repo state, so cross-repo agreement is fundamentally unenforceable
    inside a single plugin's PR checks. This out-of-band guard in Common is the agreement layer.

    Graceful degradation: a repo the token can't read (e.g. private applemusicarr under the default
    GITHUB_TOKEN) is reported as "unreadable" and EXCLUDED from the agreement set rather than failing the
    run — matching how `ecosystem-notify.yml` already omits the private repo. Provide a PAT with repo-read
    scope via $env:GH_TOKEN (CI: the ECOSYSTEM_PAT secret) to include it. When the configured streaming
    set names a known-private repo (applemusicarr) and it is unreadable, the warning calls out the PAT
    explicitly so the gap is loud rather than silent.

    Advisory repos (brainarr): reported and, when on a DIFFERENT pin than the agreed streaming SHA,
    emit a non-failing ::warning:: — but they never fail the run. brainarr is an import-list plugin
    whose Common consolidation is intentionally deferred (product decision), so it is allowed to lag;
    the warning keeps that lag visible ahead of a 5th plugin joining without breaking the deferral.

.PARAMETER StreamingRepos
    owner/repo of the streaming plugins that MUST agree. Default: the three streaming plugins.

.PARAMETER AdvisoryRepos
    owner/repo of plugins reported-but-not-enforced. Default: brainarr.

.PARAMETER CommonRepo
    owner/repo of Common, used only to report how far each plugin lags behind Common's default branch.

.PARAMETER KnownPrivateRepos
    owner/repo of repos known to be private (so an "unreadable" result is attributed to a missing PAT
    rather than a transient error). Default: applemusicarr.

.EXAMPLE
    pwsh scripts/check-ecosystem-pins.ps1
.EXAMPLE
    $env:GH_TOKEN = '<repo-read PAT>'; pwsh scripts/check-ecosystem-pins.ps1   # includes private apple
#>
[CmdletBinding()]
param(
    [string[]] $StreamingRepos    = @('RicherTunes/Qobuzarr', 'RicherTunes/Tidalarr', 'RicherTunes/AppleMusicarr'),
    [string[]] $AdvisoryRepos     = @('RicherTunes/Brainarr'),
    [string]   $CommonRepo        = 'RicherTunes/Lidarr.Plugin.Common',
    [string[]] $KnownPrivateRepos = @('RicherTunes/AppleMusicarr'),

    # Define functions and return WITHOUT touching the network. Lets unit tests dot-source this file
    # and exercise the pure decision function (Test-EcosystemPinAgreement) hermetically.
    [switch]   $DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'

# Re-pin command a drifted plugin must run (in the plugin repo) to converge. Surfaced in the failure
# message so the fix is copy-pasteable. See scripts/repin-common-submodule.ps1 in each plugin.
$script:RepinCommand = 'pwsh scripts/repin-common-submodule.ps1 -ShaFromSubmoduleHead -Stage -SubmodulePath ext/Lidarr.Plugin.Common  (after `git -C ext/Lidarr.Plugin.Common fetch && git -C ext/Lidarr.Plugin.Common checkout <SHA>`)'

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
      (sha may be $null when unreadable), returns @{ Drifted = <bool>; Pins = <distinct readable shas>;
      Agreed = <the single agreed sha or $null> }.
      Drift = two or more DISTINCT readable shas among the streaming repos.
    #>
    param([hashtable] $StreamingPins)
    $readable = @($StreamingPins.Values | Where-Object { $_ })
    $distinct = @($readable | Select-Object -Unique)
    $agreed = if ($distinct.Count -eq 1) { $distinct[0] } else { $null }
    return @{ Drifted = ($distinct.Count -gt 1); Pins = $distinct; Agreed = $agreed }
}

function Write-Summary {
    <# Append a line to the GitHub Actions job summary when running in CI; no-op locally. #>
    param([string] $Line)
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $Line
    }
}

if ($DefineFunctionsOnly) { return }

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
    $shown = if ($sha) { $sha.Substring(0, 12) } else { '<unreadable - token lacks read access>' }
    $lag = if ($sha -and $commonHead -and $sha -ne $commonHead) { ' (behind Common HEAD)' } else { '' }
    Write-Host ("    {0,-32} {1}{2}" -f $r, $shown, $lag)
}
Write-Host ""
Write-Host "  Advisory plugins (reported, never fail - consolidation deferred):"
foreach ($r in $AdvisoryRepos) {
    $sha = $advisoryPins[$r]
    $shown = if ($sha) { $sha.Substring(0, 12) } else { '<unreadable>' }
    Write-Host ("    {0,-32} {1}" -f $r, $shown)
}
Write-Host ""

Write-Summary "## Ecosystem Common-pin drift"
Write-Summary ""
if ($commonHead) { Write-Summary "Common default-branch HEAD: ``$($commonHead.Substring(0,12))``" }
Write-Summary ""
Write-Summary "| Repo | Tier | Pinned Common SHA |"
Write-Summary "| --- | --- | --- |"
foreach ($r in $StreamingRepos) {
    $sha = $streamingPins[$r]
    $shown = if ($sha) { $sha.Substring(0, 12) } else { 'unreadable' }
    Write-Summary "| ``$r`` | streaming (enforced) | ``$shown`` |"
}
foreach ($r in $AdvisoryRepos) {
    $sha = $advisoryPins[$r]
    $shown = if ($sha) { $sha.Substring(0, 12) } else { 'unreadable' }
    Write-Summary "| ``$r`` | advisory | ``$shown`` |"
}
Write-Summary ""

# --- Unreadable / PAT diagnostics ----------------------------------------------------------------
$result = Test-EcosystemPinAgreement -StreamingPins $streamingPins
$unreadable = @($StreamingRepos | Where-Object { -not $streamingPins[$_] })
if ($unreadable.Count -gt 0) {
    $unreadablePrivate = @($unreadable | Where-Object { $KnownPrivateRepos -contains $_ })
    if ($unreadablePrivate.Count -gt 0) {
        # A known-private repo (apple) is unreadable: almost always the ECOSYSTEM_PAT secret is missing.
        $msg = "Private repo(s) unreadable and EXCLUDED from the agreement check: $($unreadablePrivate -join ', '). " +
               "Configure a repo-read PAT as the ECOSYSTEM_PAT secret (CI) or `$env:GH_TOKEN (local) so these are ENFORCED, not silently skipped."
        Write-Host "::warning::$msg"
        Write-Summary "> [!WARNING]"
        Write-Summary "> $msg"
    }
    $unreadableOther = @($unreadable | Where-Object { $KnownPrivateRepos -notcontains $_ })
    if ($unreadableOther.Count -gt 0) {
        Write-Host "::warning::Could not read Common pin for: $($unreadableOther -join ', '). Excluded from the agreement check (transient error or missing read access)."
    }
}

# --- Advisory drift (brainarr): warn-but-never-fail when it lags the agreed streaming SHA ---------
if ($result.Agreed) {
    foreach ($r in $AdvisoryRepos) {
        $sha = $advisoryPins[$r]
        if ($sha -and $sha -ne $result.Agreed) {
            $msg = "Advisory plugin $r pins Common $($sha.Substring(0,12)) but the streaming set agrees on $($result.Agreed.Substring(0,12)). " +
                   "Allowed (consolidation deferred) but flagged for visibility."
            Write-Host "::warning::$msg"
            Write-Summary "> [!NOTE]"
            Write-Summary "> $msg"
        }
    }
}

# --- Verdict -------------------------------------------------------------------------------------
if ($result.Drifted) {
    # Name the drifted repos, the expected SHA (the most common pin among the streaming set), and the
    # exact re-pin command — make the failure actionable, not just an alarm.
    $pairs = @($StreamingRepos | Where-Object { $streamingPins[$_] } | ForEach-Object { [pscustomobject]@{ Repo = $_; Sha = $streamingPins[$_] } })
    $expected = ($pairs | Group-Object Sha | Sort-Object Count -Descending | Select-Object -First 1).Name
    $detail = ($pairs | ForEach-Object { "$($_.Repo)=$($_.Sha.Substring(0,12))" }) -join '; '
    $drifted = @($pairs | Where-Object { $_.Sha -ne $expected } | ForEach-Object { $_.Repo })

    Write-Host "::error::Streaming plugins pin DIFFERENT Common SHAs (drift): $detail"
    Write-Host "::error::Expected (majority) Common SHA: $expected"
    Write-Host "::error::Drifted repo(s) to re-pin: $($drifted -join ', ')"
    Write-Host "::error::Re-pin each drifted plugin to $expected, e.g.: $($script:RepinCommand)"
    Write-Host "::error::A shared-behavior fix in Common will not reach all plugins until they re-pin to one SHA."

    Write-Summary "> [!CAUTION]"
    Write-Summary "> **Ecosystem pin DRIFT detected.** Streaming plugins disagree on the Common pin."
    Write-Summary ">"
    Write-Summary "> - Pins: $detail"
    Write-Summary "> - Expected (majority) SHA: ``$expected``"
    Write-Summary "> - Drifted repo(s): $($drifted -join ', ')"
    Write-Summary "> - Re-pin each drifted plugin to ``$expected`` via: ``$($script:RepinCommand)``"
    exit 1
}

$agreed = if ($result.Agreed) { $result.Agreed.Substring(0, 12) } else { '(none readable)' }
Write-Host "OK: all readable streaming plugins agree on Common pin $agreed."
Write-Summary ""
Write-Summary "**Result:** all readable streaming plugins agree on Common pin ``$agreed``."
exit 0
