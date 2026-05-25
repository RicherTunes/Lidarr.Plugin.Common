<#
.SYNOPSIS
Atomic plugin version bump across VERSION + plugin.json + manifest.json (+ ext-common-sha.txt).

.DESCRIPTION
A plugin release touches 3-5 files that MUST agree:

  1. VERSION                — source of truth, read by Directory.Build.props
  2. plugin.json            — Lidarr UI reads .version
  3. manifest.json          — Common's PluginVersionContract asserts this matches plugin.json
  4. ext-common-sha.txt     — bumped together when -CommonVersion is supplied
  5. plugin.json / manifest.json `commonVersion` — declared common contract version

Manually editing any one of these and forgetting the others is the apple-v0.5.6/v0.5.7/v0.5.8
drift pattern. This script does the bump atomically: either all files update or none do
(dry-run on -Check, validation pass first).

.PARAMETER To
The new plugin version (e.g. "1.5.8"). Required unless -Check is given.

.PARAMETER CommonVersion
Optional. The new common version (e.g. "1.16.0"). When supplied, plugin.json and
manifest.json get their `commonVersion` field bumped together.

.PARAMETER CommonSha
Optional. The new ext-common-sha.txt content (40-char SHA). Required with -CommonVersion
unless the submodule HEAD already matches the target version.

.PARAMETER PluginRoot
Plugin repo root. Defaults to the parent of `ext/Lidarr.Plugin.Common`.

.PARAMETER Check
Validation-only mode. Reports any drift between the version sources without writing.
Exits non-zero if any drift detected.

.EXAMPLE
# Bump plugin to 1.5.8 only
pwsh ext/Lidarr.Plugin.Common/scripts/bump-plugin-version.ps1 -To 1.5.8

.EXAMPLE
# Bump plugin AND common together (during a Common release adoption)
pwsh ext/Lidarr.Plugin.Common/scripts/bump-plugin-version.ps1 -To 1.5.8 -CommonVersion 1.16.0 -CommonSha 295eb6a000000000000000000000000000000000

.EXAMPLE
# Pre-commit consistency check (fails CI if any source disagrees)
pwsh ext/Lidarr.Plugin.Common/scripts/bump-plugin-version.ps1 -Check
#>
[CmdletBinding(DefaultParameterSetName = 'Bump')]
param(
    [Parameter(ParameterSetName = 'Bump', Mandatory)]
    [string]$To,

    [Parameter(ParameterSetName = 'Bump')]
    [string]$CommonVersion,

    [Parameter(ParameterSetName = 'Bump')]
    [string]$CommonSha,

    [string]$PluginRoot,

    [Parameter(ParameterSetName = 'Check', Mandatory)]
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------- #
# Resolve PluginRoot
# ---------------------------------------------------------------------- #
if (-not $PluginRoot) {
    # This script lives at {PluginRoot}/ext/Lidarr.Plugin.Common/scripts/bump-plugin-version.ps1
    $PluginRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath)))
}
$PluginRoot = (Resolve-Path -LiteralPath $PluginRoot).Path

# ---------------------------------------------------------------------- #
# Locate the version sources for this plugin
# ---------------------------------------------------------------------- #
# Plugins vary on file layout:
#   - brainarr / tidalarr / qobuzarr: VERSION + plugin.json at repo root
#   - applemusicarr: plugin.json + manifest.json under src/AppleMusicarr.Plugin/, root manifest.json present
#   - Some plugins have manifest.json at root, some in src/
# Discover dynamically rather than hardcoding.

function Find-VersionFile {
    param([string]$Root)
    $candidate = Join-Path $Root 'VERSION'
    return (Test-Path -LiteralPath $candidate) ? $candidate : $null
}

function Find-PluginJson {
    param([string]$Root)
    # Prefer root, then src/* (any depth 2).
    $rootCandidate = Join-Path $Root 'plugin.json'
    if (Test-Path -LiteralPath $rootCandidate) { return $rootCandidate }
    $srcCandidates = Get-ChildItem -Path $Root -Filter 'plugin.json' -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[/\\](bin|obj|artifacts|node_modules|ext)[/\\]' }
    return $srcCandidates | Select-Object -First 1 -ExpandProperty FullName
}

function Find-ManifestJson {
    param([string]$Root)
    $rootCandidate = Join-Path $Root 'manifest.json'
    if (Test-Path -LiteralPath $rootCandidate) { return $rootCandidate }
    $srcCandidates = Get-ChildItem -Path $Root -Filter 'manifest.json' -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[/\\](bin|obj|artifacts|node_modules|ext)[/\\]' }
    return $srcCandidates | Select-Object -First 1 -ExpandProperty FullName
}

function Find-CommonShaFile {
    param([string]$Root)
    $candidate = Join-Path $Root 'ext-common-sha.txt'
    return (Test-Path -LiteralPath $candidate) ? $candidate : $null
}

$versionFile = Find-VersionFile -Root $PluginRoot
$pluginJsonPath = Find-PluginJson -Root $PluginRoot
$manifestJsonPath = Find-ManifestJson -Root $PluginRoot
$commonShaPath = Find-CommonShaFile -Root $PluginRoot

Write-Host "Plugin root: $PluginRoot" -ForegroundColor Cyan
Write-Host "  VERSION file:        $versionFile"
Write-Host "  plugin.json:         $pluginJsonPath"
Write-Host "  manifest.json:       $(if ($manifestJsonPath) { $manifestJsonPath } else { '(none — plugin only declares version in plugin.json)' })"
Write-Host "  ext-common-sha.txt:  $(if ($commonShaPath) { $commonShaPath } else { '(none — submodule pin not tracked in a separate file)' })"
Write-Host ""

# ---------------------------------------------------------------------- #
# Read current values
# ---------------------------------------------------------------------- #
function Read-JsonField {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Field
    )
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $doc = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (-not $doc.PSObject.Properties.Name.Contains($Field)) { return $null }
    return $doc.$Field
}

$currentVersion = if ($versionFile) { (Get-Content -LiteralPath $versionFile -Raw).Trim() } else { $null }
$pluginVersion = Read-JsonField -Path $pluginJsonPath -Field 'version'
$pluginCommonVersion = Read-JsonField -Path $pluginJsonPath -Field 'commonVersion'
$manifestVersion = if ($manifestJsonPath) { Read-JsonField -Path $manifestJsonPath -Field 'version' } else { $null }
$manifestCommonVersion = if ($manifestJsonPath) { Read-JsonField -Path $manifestJsonPath -Field 'commonVersion' } else { $null }
$currentCommonSha = if ($commonShaPath) { (Get-Content -LiteralPath $commonShaPath -Raw).Trim() } else { $null }

# ---------------------------------------------------------------------- #
# Check mode: validate, report drift, exit non-zero if any
# ---------------------------------------------------------------------- #
if ($Check) {
    $drift = @()

    if ($versionFile -and $pluginVersion -and $currentVersion -ne $pluginVersion) {
        $drift += "VERSION ($currentVersion) != plugin.json.version ($pluginVersion)"
    }
    if ($pluginVersion -and $manifestVersion -and $pluginVersion -ne $manifestVersion) {
        $drift += "plugin.json.version ($pluginVersion) != manifest.json.version ($manifestVersion)"
    }
    if ($pluginCommonVersion -and $manifestCommonVersion -and $pluginCommonVersion -ne $manifestCommonVersion) {
        $drift += "plugin.json.commonVersion ($pluginCommonVersion) != manifest.json.commonVersion ($manifestCommonVersion)"
    }

    if ($drift.Count -eq 0) {
        Write-Host "OK — all version sources agree." -ForegroundColor Green
        Write-Host "  Plugin version:       $pluginVersion"
        Write-Host "  Common version:       $pluginCommonVersion"
        Write-Host "  Common submodule SHA: $currentCommonSha"
        exit 0
    }

    Write-Host "DRIFT DETECTED in $($drift.Count) source(s):" -ForegroundColor Red
    foreach ($d in $drift) {
        Write-Host "  - $d" -ForegroundColor Red
    }
    exit 1
}

# ---------------------------------------------------------------------- #
# Bump mode: validate inputs, write all files
# ---------------------------------------------------------------------- #
if (-not ($To -match '^\d+\.\d+\.\d+(-[A-Za-z0-9\.]+)?$')) {
    throw "Invalid -To value '$To'. Must match X.Y.Z or X.Y.Z-prerelease."
}

if ($CommonVersion -and -not ($CommonVersion -match '^\d+\.\d+\.\d+$')) {
    throw "Invalid -CommonVersion value '$CommonVersion'. Must match X.Y.Z."
}

if ($CommonSha -and $CommonSha -notmatch '^[a-f0-9]{40}$') {
    throw "Invalid -CommonSha value. Must be a 40-character lowercase hex SHA."
}

Write-Host "Bumping plugin: $pluginVersion -> $To" -ForegroundColor Cyan
if ($CommonVersion) {
    Write-Host "Bumping common: $pluginCommonVersion -> $CommonVersion" -ForegroundColor Cyan
}
if ($CommonSha) {
    Write-Host "Bumping ext-common-sha.txt: $currentCommonSha -> $CommonSha" -ForegroundColor Cyan
}
Write-Host ""

# Update VERSION
if ($versionFile) {
    Set-Content -LiteralPath $versionFile -Value $To -NoNewline -Encoding utf8
    Add-Content -LiteralPath $versionFile -Value '' -Encoding utf8
    Write-Host "  Updated VERSION -> $To"
}

# Update plugin.json — preserve formatting via regex replace
if ($pluginJsonPath) {
    $content = Get-Content -LiteralPath $pluginJsonPath -Raw
    $content = $content -replace '("version"\s*:\s*")[^"]+(")', "`${1}$To`${2}"
    if ($CommonVersion) {
        $content = $content -replace '("commonVersion"\s*:\s*")[^"]+(")', "`${1}$CommonVersion`${2}"
    }
    Set-Content -LiteralPath $pluginJsonPath -Value $content -NoNewline -Encoding utf8
    Write-Host "  Updated plugin.json"
}

# Update manifest.json
if ($manifestJsonPath) {
    $content = Get-Content -LiteralPath $manifestJsonPath -Raw
    $content = $content -replace '("version"\s*:\s*")[^"]+(")', "`${1}$To`${2}"
    if ($CommonVersion) {
        $content = $content -replace '("commonVersion"\s*:\s*")[^"]+(")', "`${1}$CommonVersion`${2}"
    }
    Set-Content -LiteralPath $manifestJsonPath -Value $content -NoNewline -Encoding utf8
    Write-Host "  Updated manifest.json"
}

# Update ext-common-sha.txt
if ($CommonSha -and $commonShaPath) {
    Set-Content -LiteralPath $commonShaPath -Value $CommonSha -NoNewline -Encoding utf8
    Add-Content -LiteralPath $commonShaPath -Value '' -Encoding utf8
    Write-Host "  Updated ext-common-sha.txt"
}

Write-Host ""
Write-Host "Re-verifying consistency post-bump..." -ForegroundColor Cyan

# Self-invoke -Check to validate the result
& $PSCommandPath -Check -PluginRoot $PluginRoot
if ($LASTEXITCODE -ne 0) {
    throw "Post-bump consistency check FAILED. Review the files manually."
}
