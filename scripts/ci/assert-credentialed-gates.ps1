#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validates that credentialed gates actually ran (no silent green-by-skip).

.DESCRIPTION
    CI-only assertion script called from e2e-bootstrap.yml when credentialed gates
    (Search, Grab, ImportList) are explicitly requested.

    When run_search_gate=true or run_grab_gate=true:
    - summary.skipped must be 0 (no gates silently skipped due to missing creds)
    - summary.failed must be 0 (all gates passed)

    When run_grab_gate=true:
    - At least one Grab result must exist with outcome=success for each streaming plugin

    This prevents "silent green" where CI passes because gates were skipped, not
    because they actually succeeded.

    Exit codes:
    0: assertions passed
    1: assertions failed
#>

param(
    [Parameter(Mandatory)]
    [string]$ManifestPath,

    [Parameter(Mandatory)]
    [string]$Plugins,

    [string]$RunSearchGate,
    [string]$RunGrabGate,
    [string]$RunImportListGate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Is-Truthy {
    param([object]$Value)
    if ($null -eq $Value) { return $false }
    $s = "$Value".Trim().ToLowerInvariant()
    return @('1', 'true', 'yes', 'y', 'on') -contains $s
}

function Fail {
    param([string[]]$Messages)
    Write-Host "::error::Credentialed gate assertion failed with $($Messages.Count) issue(s):"
    foreach ($m in $Messages) {
        Write-Host "  - $m" -ForegroundColor Red
    }
    exit 1
}

$searchRequested = Is-Truthy $RunSearchGate
$grabRequested = Is-Truthy $RunGrabGate
$importListRequested = Is-Truthy $RunImportListGate

# Only run assertions if credentialed gates were explicitly requested
if (-not ($searchRequested -or $grabRequested -or $importListRequested)) {
    Write-Host "No credentialed gates requested; skipping assertions." -ForegroundColor Yellow
    exit 0
}

if (-not (Test-Path $ManifestPath)) {
    Fail @("Manifest not found: $ManifestPath")
}

$pluginList = @(
    $Plugins -split ',' |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)

# Filter to streaming plugins only (Qobuzarr, Tidalarr) - they're the ones with grab gates
$streamingPlugins = @($pluginList | Where-Object { $_ -in @('Qobuzarr', 'Tidalarr', 'AppleMusicarr') })

if ($pluginList.Count -eq 0) {
    Fail @("Plugins list is empty.")
}

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
if (-not $manifest.schemaVersion) {
    Fail @("Manifest missing schemaVersion.")
}

$summary = $manifest.summary
if (-not $summary) {
    Fail @("Manifest missing summary section.")
}

$failures = New-Object System.Collections.Generic.List[string]

# Rule 1: When credentialed gates are requested, no gates should be skipped
if ($summary.skipped -gt 0) {
    $skippedResults = @($manifest.results | Where-Object { $_.outcome -eq 'skipped' })
    $skippedDesc = ($skippedResults | ForEach-Object { "$($_.plugin)/$($_.gate)" }) -join ', '
    $failures.Add("summary.skipped=$($summary.skipped) but credentialed gates were requested. Skipped: [$skippedDesc]. This likely means secrets are missing.")
}

# Rule 2: When credentialed gates are requested, no gates should fail
if ($summary.failed -gt 0) {
    $failedResults = @($manifest.results | Where-Object { $_.outcome -eq 'failed' })
    $failedDesc = ($failedResults | ForEach-Object {
        $code = if ($_.errorCode) { " ($($_.errorCode))" } else { "" }
        "$($_.plugin)/$($_.gate)$code"
    }) -join ', '
    $failures.Add("summary.failed=$($summary.failed). Failed gates: [$failedDesc].")
}

# Rule 3: When run_grab_gate=true, each streaming plugin must have a successful Grab
if ($grabRequested -and $streamingPlugins.Count -gt 0) {
    foreach ($plugin in $streamingPlugins) {
        $grabResults = @($manifest.results | Where-Object { $_.plugin -eq $plugin -and $_.gate -eq 'Grab' })

        if ($grabResults.Count -eq 0) {
            $failures.Add("[$plugin] run_grab_gate=true but no Grab gate result found in manifest.")
            continue
        }

        $successfulGrab = $grabResults | Where-Object { $_.outcome -eq 'success' } | Select-Object -First 1
        if (-not $successfulGrab) {
            $outcomes = ($grabResults | ForEach-Object { $_.outcome }) -join ', '
            $failures.Add("[$plugin] run_grab_gate=true but no Grab gate with outcome=success. Found outcomes: [$outcomes].")
        }
    }
}

# Rule 4: When run_search_gate=true, each streaming plugin must have a successful Search
if ($searchRequested -and $streamingPlugins.Count -gt 0) {
    foreach ($plugin in $streamingPlugins) {
        $searchResults = @($manifest.results | Where-Object { $_.plugin -eq $plugin -and $_.gate -eq 'Search' })

        if ($searchResults.Count -eq 0) {
            $failures.Add("[$plugin] run_search_gate=true but no Search gate result found in manifest.")
            continue
        }

        $successfulSearch = $searchResults | Where-Object { $_.outcome -eq 'success' } | Select-Object -First 1
        if (-not $successfulSearch) {
            $outcomes = ($searchResults | ForEach-Object { $_.outcome }) -join ', '
            $failures.Add("[$plugin] run_search_gate=true but no Search gate with outcome=success. Found outcomes: [$outcomes].")
        }
    }
}

# Rule 5: When run_importlist_gate=true, each applicable plugin must have a successful ImportList
if ($importListRequested) {
    # ImportList applies to all plugins that support it
    $importListPlugins = @($pluginList | Where-Object { $_ -in @('AppleMusicarr', 'Brainarr') })

    foreach ($plugin in $importListPlugins) {
        $importListResults = @($manifest.results | Where-Object { $_.plugin -eq $plugin -and $_.gate -eq 'ImportList' })

        if ($importListResults.Count -eq 0) {
            $failures.Add("[$plugin] run_importlist_gate=true but no ImportList gate result found in manifest.")
            continue
        }

        $successfulImportList = $importListResults | Where-Object { $_.outcome -eq 'success' } | Select-Object -First 1
        if (-not $successfulImportList) {
            $outcomes = ($importListResults | ForEach-Object { $_.outcome }) -join ', '
            $failures.Add("[$plugin] run_importlist_gate=true but no ImportList gate with outcome=success. Found outcomes: [$outcomes].")
        }
    }
}

if ($failures.Count -gt 0) {
    Fail $failures.ToArray()
}

Write-Host "Credentialed gate assertions passed" -ForegroundColor Green
Write-Host "  - Search requested: $searchRequested"
Write-Host "  - Grab requested: $grabRequested"
Write-Host "  - ImportList requested: $importListRequested"
Write-Host "  - Passed: $($summary.passed), Failed: $($summary.failed), Skipped: $($summary.skipped)"
exit 0
