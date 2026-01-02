#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validates cold-start E2E expectations against a run-manifest.json file.

.DESCRIPTION
    CI-only assertion script intended to be called from
    `lidarr.plugin.common/.github/workflows/e2e-bootstrap.yml` when `cold_start=true`.

    It verifies:
    - Schema gate always succeeds for requested plugins.
    - Configure gate is SKIP when credentials are absent.
    - Configure gate is SUCCESS when credentials are present.
    - No component ID persistence is attempted/updated when no credentials exist.

    Exit codes:
    0: assertions passed
    1: assertions failed
#>

param(
    [Parameter(Mandatory)]
    [string]$ManifestPath,

    [Parameter(Mandatory)]
    [string]$Plugins,

    [string]$HasQobuzCreds,
    [string]$HasTidalCreds,
    [string]$HasBrainarrCreds
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
    Write-Host "::error::Cold-start assertion failed with $($Messages.Count) issue(s):"
    foreach ($m in $Messages) {
        Write-Host "  - $m" -ForegroundColor Red
    }
    exit 1
}

if (-not (Test-Path $ManifestPath)) {
    Fail @("Manifest not found: $ManifestPath")
}

$pluginList = @(
    $Plugins -split ',' |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)

if ($pluginList.Count -eq 0) {
    Fail @("Plugins list is empty.")
}

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
if (-not $manifest.schemaVersion) {
    Fail @("Manifest missing schemaVersion.")
}

$hasCredsByPlugin = @{
    Qobuzarr = (Is-Truthy $HasQobuzCreds)
    Tidalarr = (Is-Truthy $HasTidalCreds)
    Brainarr = (Is-Truthy $HasBrainarrCreds)
}

function Get-GateResult {
    param(
        [Parameter(Mandatory)][string]$Plugin,
        [Parameter(Mandatory)][string]$Gate
    )

    $matches = @($manifest.results | Where-Object { $_.plugin -eq $Plugin -and $_.gate -eq $Gate })
    if ($matches.Count -eq 0) { return $null }
    if ($matches.Count -gt 1) {
        Fail @("Multiple manifest results for plugin '$Plugin' gate '$Gate' (count=$($matches.Count)).")
    }
    return $matches[0]
}

function Get-DetailBool {
    param(
        [object]$Details,
        [Parameter(Mandatory)][string]$Name
    )
    if ($null -eq $Details) { return $false }
    $props = $Details.PSObject.Properties
    if ($null -eq $props) { return $false }
    $prop = $props[$Name]
    if ($null -eq $prop) { return $false }
    return $prop.Value -eq $true
}

$failures = New-Object System.Collections.Generic.List[string]

foreach ($plugin in $pluginList) {
    $schema = Get-GateResult -Plugin $plugin -Gate 'Schema'
    if (-not $schema) {
        $failures.Add("[$plugin] Missing Schema gate result.")
        continue
    }
    if ($schema.outcome -ne 'success') {
        $failures.Add("[$plugin] Schema gate expected outcome=success, got '$($schema.outcome)'.")
    }

    $configure = Get-GateResult -Plugin $plugin -Gate 'Configure'
    if (-not $configure) {
        $failures.Add("[$plugin] Missing Configure gate result.")
        continue
    }

    $hasCreds = $hasCredsByPlugin.ContainsKey($plugin) -and $hasCredsByPlugin[$plugin]
    if ($hasCreds) {
        if ($configure.outcome -ne 'success') {
            $failures.Add("[$plugin] Configure gate expected outcome=success (credentials provided), got '$($configure.outcome)'.")
        }

        $details = $configure.details
        $created = Get-DetailBool -Details $details -Name 'created'
        $alreadyConfigured = Get-DetailBool -Details $details -Name 'alreadyConfigured'
        if (-not ($created -or $alreadyConfigured)) {
            $failures.Add("[$plugin] Configure gate expected details.created=true or details.alreadyConfigured=true when credentials are provided.")
        }
    }
    else {
        if ($configure.outcome -ne 'skipped') {
            $failures.Add("[$plugin] Configure gate expected outcome=skipped (no credentials), got '$($configure.outcome)'.")
        }

        $details = $configure.details
        if ($details) {
            if (Get-DetailBool -Details $details -Name 'created') {
                $failures.Add("[$plugin] Configure gate must not create components without credentials (details.created=true).")
            }
            if (Get-DetailBool -Details $details -Name 'updated') {
                $failures.Add("[$plugin] Configure gate must not update components without credentials (details.updated=true).")
            }
        }
    }
}

# Persistence expectations (run-level):
# If *no* plugin creds exist, persistence must not be attempted.
$anyCreds = $hasCredsByPlugin.Values | Where-Object { $_ } | Select-Object -First 1
$componentIds = $manifest.componentIds
if (-not $anyCreds) {
    if ($componentIds) {
        if ($componentIds.persistedIdsUpdateAttempted -eq $true) {
            $failures.Add("componentIds.persistedIdsUpdateAttempted must be false when no credentials are provided.")
        }
        if ($componentIds.persistedIdsUpdated -eq $true) {
            $failures.Add("componentIds.persistedIdsUpdated must be false when no credentials are provided.")
        }
        $reason = "$($componentIds.persistedIdsUpdateReason)".Trim()
        if ($reason -and @('disabled', 'not_eligible', 'unknown') -notcontains $reason) {
            $failures.Add("componentIds.persistedIdsUpdateReason='$reason' unexpected when no credentials are provided.")
        }
    }
}

if ($failures.Count -gt 0) {
    Fail $failures.ToArray()
}

Write-Host "Cold-start assertions passed" -ForegroundColor Green
exit 0
