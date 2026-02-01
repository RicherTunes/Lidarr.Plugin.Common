#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks drift sentinel artifacts to determine if providers are ready for strict mode.

.DESCRIPTION
    Reads drift-sentinel.json artifacts from recent nightly runs and computes:
    - Pass streak per provider (consecutive nights without drift)
    - Inconclusive rate (429/rate-limited probes)
    - Reliability issue status
    - Promotion readiness

.PARAMETER ArtifactPath
    Path to directory containing drift artifacts (or single artifact file).
    Default: searches for drift-sentinel.json in current directory tree.

.PARAMETER Provider
    Specific provider to check (qobuz, tidal). Default: all providers.

.PARAMETER QobuzThreshold
    Consecutive clean runs required for Qobuz promotion. Default: 5

.PARAMETER TidalThreshold
    Consecutive clean runs required for Tidal promotion. Default: 7

.PARAMETER MaxInconclusivePercent
    Maximum inconclusive percentage allowed for promotion. Default: 10

.PARAMETER OutputJson
    Output results as JSON instead of formatted text.

.EXAMPLE
    ./drift-strictness-check.ps1
    # Check all artifacts in current directory

.EXAMPLE
    ./drift-strictness-check.ps1 -ArtifactPath ./artifacts -Provider qobuz
    # Check Qobuz readiness from specific artifact directory

.EXAMPLE
    ./drift-strictness-check.ps1 -OutputJson | ConvertFrom-Json
    # Get machine-readable results
#>

param(
    [string]$ArtifactPath = ".",
    [ValidateSet("qobuz", "tidal", "all")]
    [string]$Provider = "all",
    [int]$QobuzThreshold = 5,
    [int]$TidalThreshold = 7,
    [int]$MaxInconclusivePercent = 10,
    [switch]$OutputJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#region Find and Load Artifacts

function Find-DriftArtifacts {
    param([string]$Path)

    $artifacts = @()

    if (Test-Path $Path -PathType Leaf) {
        # Single file
        if ($Path -match "drift-sentinel\.json$") {
            $artifacts += $Path
        }
    }
    elseif (Test-Path $Path -PathType Container) {
        # Directory - search recursively
        $artifacts = Get-ChildItem -Path $Path -Recurse -Filter "drift-sentinel.json" -File |
            Select-Object -ExpandProperty FullName
    }

    return $artifacts
}

function Load-DriftArtifact {
    param([string]$Path)

    try {
        $content = Get-Content -Path $Path -Raw | ConvertFrom-Json
        return $content
    }
    catch {
        Write-Warning "Failed to load artifact: $Path - $($_.Exception.Message)"
        return $null
    }
}

#endregion

#region Analysis Functions

function Get-ProviderResults {
    param(
        [array]$Artifacts,
        [string]$ProviderName
    )

    $results = @()

    foreach ($artifact in $Artifacts) {
        $timestamp = $artifact.timestamp
        $version = $artifact.expectationsVersion

        # Find probes for this provider
        $providerProbes = $artifact.probes | Where-Object { $_.provider -eq $ProviderName }

        if ($providerProbes.Count -eq 0) {
            continue
        }

        $hasDrift = ($providerProbes | Where-Object { $_.driftDetected }).Count -gt 0
        $hasError = ($providerProbes | Where-Object { $_.hasError }).Count -gt 0
        $inconclusiveCount = ($providerProbes | Where-Object { $_.isInconclusive }).Count
        $totalProbes = $providerProbes.Count
        $inconclusivePercent = if ($totalProbes -gt 0) { [math]::Round(($inconclusiveCount / $totalProbes) * 100, 1) } else { 0 }

        $results += [PSCustomObject]@{
            Timestamp = $timestamp
            Version = $version
            Provider = $ProviderName
            HasDrift = $hasDrift
            HasError = $hasError
            InconclusiveCount = $inconclusiveCount
            TotalProbes = $totalProbes
            InconclusivePercent = $inconclusivePercent
            IsClean = (-not $hasDrift -and -not $hasError)
        }
    }

    # Sort by timestamp descending (most recent first)
    return $results | Sort-Object -Property Timestamp -Descending
}

function Get-PassStreak {
    param([array]$Results)

    $streak = 0
    foreach ($result in $Results) {
        if ($result.IsClean) {
            $streak++
        }
        else {
            break
        }
    }
    return $streak
}

function Get-AverageInconclusiveRate {
    param([array]$Results, [int]$LastN = 7)

    $recent = $Results | Select-Object -First $LastN
    if ($recent.Count -eq 0) { return 0 }

    $totalProbes = ($recent | Measure-Object -Property TotalProbes -Sum).Sum
    $totalInconclusive = ($recent | Measure-Object -Property InconclusiveCount -Sum).Sum

    if ($totalProbes -eq 0) { return 0 }
    return [math]::Round(($totalInconclusive / $totalProbes) * 100, 1)
}

function Test-PromotionReady {
    param(
        [string]$ProviderName,
        [int]$PassStreak,
        [double]$InconclusiveRate,
        [int]$Threshold,
        [int]$MaxInconclusive
    )

    $reasons = @()

    if ($PassStreak -lt $Threshold) {
        $reasons += "Pass streak ($PassStreak) < threshold ($Threshold)"
    }

    if ($InconclusiveRate -gt $MaxInconclusive) {
        $reasons += "Inconclusive rate ($InconclusiveRate%) > max ($MaxInconclusive%)"
    }

    return [PSCustomObject]@{
        Ready = ($reasons.Count -eq 0)
        Reasons = $reasons
    }
}

#endregion

#region Main

# Find artifacts
$artifactFiles = Find-DriftArtifacts -Path $ArtifactPath

if ($artifactFiles.Count -eq 0) {
    if ($OutputJson) {
        @{ error = "No drift-sentinel.json artifacts found"; path = $ArtifactPath } | ConvertTo-Json
    }
    else {
        Write-Host "No drift-sentinel.json artifacts found in: $ArtifactPath" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "To get artifacts, download from GitHub Actions:"
        Write-Host "  gh run download <run-id> -n drift-summary"
        Write-Host ""
        Write-Host "Or run drift sentinel locally:"
        Write-Host "  ./scripts/multi-plugin-docker-smoke-test.ps1 -RunDriftSentinelGate"
    }
    exit 1
}

# Load artifacts
$artifacts = @()
foreach ($file in $artifactFiles) {
    $artifact = Load-DriftArtifact -Path $file
    if ($artifact) {
        $artifacts += $artifact
    }
}

if ($artifacts.Count -eq 0) {
    Write-Host "No valid artifacts loaded" -ForegroundColor Red
    exit 1
}

# Sort by timestamp
$artifacts = $artifacts | Sort-Object -Property timestamp -Descending

# Determine providers to check
$providersToCheck = @()
if ($Provider -eq "all") {
    $providersToCheck = @("qobuz", "tidal")
}
else {
    $providersToCheck = @($Provider)
}

# Build results
$checkResults = @{}

foreach ($prov in $providersToCheck) {
    $threshold = if ($prov -eq "qobuz") { $QobuzThreshold } else { $TidalThreshold }

    $provResults = Get-ProviderResults -Artifacts $artifacts -ProviderName $prov
    $passStreak = Get-PassStreak -Results $provResults
    $inconclusiveRate = Get-AverageInconclusiveRate -Results $provResults -LastN $threshold
    $promotion = Test-PromotionReady -ProviderName $prov -PassStreak $passStreak -InconclusiveRate $inconclusiveRate -Threshold $threshold -MaxInconclusive $MaxInconclusivePercent

    $recentRuns = $provResults | Select-Object -First 5 | ForEach-Object {
        [PSCustomObject]@{
            date = ([datetime]$_.Timestamp).ToString("yyyy-MM-dd")
            clean = $_.IsClean
            drift = $_.HasDrift
            inconclusive = "$($_.InconclusivePercent)%"
        }
    }

    $checkResults[$prov] = [PSCustomObject]@{
        provider = $prov
        threshold = $threshold
        passStreak = $passStreak
        inconclusiveRate = $inconclusiveRate
        maxInconclusiveAllowed = $MaxInconclusivePercent
        ready = $promotion.Ready
        blockers = $promotion.Reasons
        recentRuns = $recentRuns
        recommendation = if ($promotion.Ready) {
            "READY: Promote to strict mode"
        }
        else {
            "NOT READY: $($promotion.Reasons -join '; ')"
        }
    }
}

# Output
if ($OutputJson) {
    [PSCustomObject]@{
        timestamp = [datetime]::UtcNow.ToString("o")
        artifactsAnalyzed = $artifacts.Count
        providers = $checkResults
    } | ConvertTo-Json -Depth 5
}
else {
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  DRIFT SENTINEL STRICTNESS CHECK" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Artifacts analyzed: $($artifacts.Count)" -ForegroundColor DarkGray
    Write-Host ""

    foreach ($prov in $providersToCheck) {
        $result = $checkResults[$prov]

        $statusColor = if ($result.ready) { "Green" } else { "Yellow" }
        $statusIcon = if ($result.ready) { "[READY]" } else { "[NOT READY]" }

        Write-Host "───────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
        Write-Host "  $($prov.ToUpper()) $statusIcon" -ForegroundColor $statusColor
        Write-Host "───────────────────────────────────────────────────────────────" -ForegroundColor DarkGray
        Write-Host ""
        Write-Host "  Pass streak:        $($result.passStreak) / $($result.threshold) required" -ForegroundColor $(if ($result.passStreak -ge $result.threshold) { "Green" } else { "Yellow" })
        Write-Host "  Inconclusive rate:  $($result.inconclusiveRate)% (max $($result.maxInconclusiveAllowed)%)" -ForegroundColor $(if ($result.inconclusiveRate -le $result.maxInconclusiveAllowed) { "Green" } else { "Yellow" })
        Write-Host ""

        if ($result.blockers.Count -gt 0) {
            Write-Host "  Blockers:" -ForegroundColor Yellow
            foreach ($blocker in $result.blockers) {
                Write-Host "    - $blocker" -ForegroundColor Yellow
            }
            Write-Host ""
        }

        Write-Host "  Recent runs:" -ForegroundColor DarkGray
        foreach ($run in $result.recentRuns) {
            $runStatus = if ($run.clean) { "clean" } elseif ($run.drift) { "DRIFT" } else { "error" }
            $runColor = if ($run.clean) { "Green" } else { "Red" }
            Write-Host "    $($run.date): $runStatus (inconclusive: $($run.inconclusive))" -ForegroundColor $runColor
        }
        Write-Host ""

        Write-Host "  Recommendation: $($result.recommendation)" -ForegroundColor $statusColor
        Write-Host ""
    }

    # Print promotion command if ready
    $readyProviders = $providersToCheck | Where-Object { $checkResults[$_].ready }
    if ($readyProviders.Count -gt 0) {
        Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
        Write-Host "  PROMOTION INSTRUCTIONS" -ForegroundColor Cyan
        Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "To promote to strict mode, edit .github/workflows/e2e-nightly-live.yml:" -ForegroundColor White
        Write-Host ""
        Write-Host "  drift_sentinel_fail_on_drift: true" -ForegroundColor Green
        Write-Host ""
        Write-Host "Note: This will fail nightly if ANY provider drifts. For per-provider" -ForegroundColor DarkGray
        Write-Host "strictness, implement provider-specific fail_on_drift flags." -ForegroundColor DarkGray
        Write-Host ""
    }
}

#endregion
