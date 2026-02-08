#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates or verifies packaging/expected-contents.txt from a plugin ZIP.

.DESCRIPTION
    Reads a plugin ZIP and an optional existing manifest to produce a sorted
    expected-contents.txt file. Designed to keep the manifest in sync with
    actual package output, preventing manual edit drift.

    Modes:
      -Check   Compare ZIP against existing manifest; exit 1 on mismatch.
      -Update  Overwrite the manifest REQUIRED section from the ZIP.
      (default) Print a diff/report to stdout without writing anything.

.PARAMETER ZipPath
    Path to the plugin ZIP file to inspect.

.PARAMETER ManifestPath
    Path to packaging/expected-contents.txt. Defaults to packaging/expected-contents.txt
    relative to the current directory.

.PARAMETER PluginName
    Plugin display name for the header comment (e.g. "Brainarr"). Auto-detected from
    the main DLL name if omitted.

.PARAMETER Check
    CI mode: exit 0 if manifest matches ZIP, exit 1 if drift detected.

.PARAMETER Update
    Write mode: overwrite the REQUIRED section in the manifest from the ZIP contents.
    Preserves the FORBIDDEN section and comments.

.EXAMPLE
    # Report mode (default): show what would change
    ./scripts/generate-expected-contents.ps1 -ZipPath artifacts/packages/brainarr-1.3.2-net8.0.zip

.EXAMPLE
    # CI check mode
    ./scripts/generate-expected-contents.ps1 -ZipPath plugin.zip -Check

.EXAMPLE
    # Update manifest from ZIP
    ./scripts/generate-expected-contents.ps1 -ZipPath plugin.zip -Update
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$ZipPath,

    [string]$ManifestPath = 'packaging/expected-contents.txt',

    [string]$PluginName,

    [switch]$Check,

    [switch]$Update
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Validate inputs ---

if (-not (Test-Path -LiteralPath $ZipPath)) {
    Write-Error "ZIP not found: $ZipPath"
    exit 1
}

# --- Read ZIP entries (leaf file names only) ---

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $ZipPath))
try {
    $entries = @($zip.Entries | ForEach-Object { $_.Name } | Where-Object { $_ -ne '' } | Sort-Object -Unique)
}
finally {
    $zip.Dispose()
}

if ($entries.Count -eq 0) {
    Write-Error "ZIP is empty: $ZipPath"
    exit 1
}

# --- Classify ZIP contents ---

# Standard forbidden list (host-provided assemblies that cause type-identity conflicts)
$standardForbidden = @(
    'FluentValidation.dll'
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll'
    'Microsoft.Extensions.Logging.Abstractions.dll'
    'NLog.dll'
    'System.Text.Json.dll'
    'Lidarr.Core.dll'
    'Lidarr.Http.dll'
    'Lidarr.Api.V1.dll'
    'Lidarr.Common.dll'
    'NzbDrone.Common.dll'
    'NzbDrone.Core.dll'
    'NzbDrone.SignalR.dll'
)

$forbiddenLower = @($standardForbidden | ForEach-Object { $_.ToLowerInvariant() })

# --- Parse existing manifest (if present) ---

$existingRequired = @()
$existingForbidden = @()
$existingComments = @{}
$hasExisting = Test-Path -LiteralPath $ManifestPath

if ($hasExisting) {
    $section = $null
    $commentBuffer = @()
    Get-Content -LiteralPath $ManifestPath | ForEach-Object {
        $line = $_.Trim()
        if ($line -eq '[REQUIRED]') { $section = 'required'; return }
        if ($line -eq '[FORBIDDEN]') {
            $section = 'forbidden'
            $commentBuffer = @()
            return
        }
        if ($line -eq '' -or $line.StartsWith('#')) {
            if ($section -eq 'forbidden') { $commentBuffer += $_ }
            return
        }
        if ($section -eq 'required') { if ($line -notin $existingRequired) { $existingRequired += $line } }
        if ($section -eq 'forbidden') {
            # Preserve comment immediately above this entry
            if ($commentBuffer.Count -gt 0) {
                $existingComments[$line] = $commentBuffer
                $commentBuffer = @()
            }
            $existingForbidden += $line
        }
    }
}

# --- Compute violations using effective forbidden list ---
# Use the manifest's FORBIDDEN section if present (may have additional entries beyond standard);
# fall back to standard list for fresh generation.
$effectiveForbidden = if ($existingForbidden.Count -gt 0) { $existingForbidden } else { $standardForbidden }
$effectiveForbiddenLower = @($effectiveForbidden | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique)
$violations = @($entries | Where-Object { $_.ToLowerInvariant() -in $effectiveForbiddenLower })
$requiredFromZip = @($entries | Where-Object { $_.ToLowerInvariant() -notin $effectiveForbiddenLower } | Sort-Object -Unique)

# Auto-detect plugin name from main DLL
if (-not $PluginName) {
    $mainDll = $requiredFromZip | Where-Object { $_ -match '\.dll$' -and $_ -notmatch 'Abstractions' } | Select-Object -First 1
    if ($mainDll) {
        $PluginName = [System.IO.Path]::GetFileNameWithoutExtension($mainDll) -replace '^Lidarr\.Plugin\.', '' -replace '\.Plugin$', ''
    }
    else {
        $PluginName = 'Plugin'
    }
}

# --- Mode: Report (default) ---

function Show-Report {
    Write-Host "`nZIP: $ZipPath ($($entries.Count) files)" -ForegroundColor Cyan
    Write-Host "Manifest: $(if ($hasExisting) { $ManifestPath } else { '(not found)' })" -ForegroundColor Cyan
    Write-Host ""

    $entriesLower = @($entries | ForEach-Object { $_.ToLowerInvariant() })
    $existingRequiredLower = @($existingRequired | ForEach-Object { $_.ToLowerInvariant() })

    # REQUIRED missing from ZIP (gate failure)
    $missingReq = @($existingRequired | Where-Object { $_.ToLowerInvariant() -notin $entriesLower })

    # Extras: in ZIP but not in REQUIRED or FORBIDDEN (informational only)
    $extras = @($entries | Where-Object {
        $_.ToLowerInvariant() -notin $existingRequiredLower -and
        $_.ToLowerInvariant() -notin $forbiddenLower
    })

    $hasFailure = ($missingReq.Count -gt 0) -or ($violations.Count -gt 0)

    if (-not $hasFailure -and $extras.Count -eq 0) {
        Write-Host "Manifest is in sync with ZIP." -ForegroundColor Green
        return $true
    }

    if ($missingReq.Count -gt 0) {
        Write-Host "REQUIRED files missing from ZIP:" -ForegroundColor Red
        $missingReq | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    }
    if ($violations.Count -gt 0) {
        Write-Host "FORBIDDEN files found in ZIP:" -ForegroundColor Red
        $violations | ForEach-Object { Write-Host "  ! $_" -ForegroundColor Red }
    }
    if ($extras.Count -gt 0) {
        Write-Host "Extra files in ZIP (informational, not gated):" -ForegroundColor DarkGray
        $extras | ForEach-Object { Write-Host "  ~ $_" -ForegroundColor DarkGray }
    }

    # Only fail on missing REQUIRED or present FORBIDDEN, not extras
    return -not $hasFailure
}

# --- Mode: Generate manifest text ---

function Get-ManifestText {
    $lines = @()
    $lines += "# $PluginName Plugin Package -- Expected Contents"
    $lines += '# This file is the source of truth for what the plugin ZIP must contain.'
    $lines += '# CI compares actual package contents against this manifest.'
    $lines += '# To add a new file, update this manifest AND the code in the same PR.'
    $lines += '#'
    $lines += '# Format: [REQUIRED] files that MUST be present; [FORBIDDEN] files that MUST NOT be present.'
    $lines += ''
    $lines += '[REQUIRED]'
    foreach ($r in $requiredFromZip) {
        $lines += $r
    }
    $lines += ''
    $lines += '[FORBIDDEN]'
    $lines += '# Host-provided contract assemblies -- including these causes type-identity conflicts'

    # Use existing forbidden section with comments if available, otherwise standard list
    if ($existingForbidden.Count -gt 0) {
        # Rebuild forbidden section preserving comments
        $forbiddenOutput = @()
        foreach ($f in $existingForbidden) {
            if ($existingComments.ContainsKey($f)) {
                foreach ($c in $existingComments[$f]) {
                    $forbiddenOutput += $c
                }
            }
            $forbiddenOutput += $f
        }
        $lines += $forbiddenOutput
    }
    else {
        $lines += 'FluentValidation.dll'
        $lines += 'Microsoft.Extensions.DependencyInjection.Abstractions.dll'
        $lines += 'Microsoft.Extensions.Logging.Abstractions.dll'
        $lines += 'NLog.dll'
        $lines += 'System.Text.Json.dll'
        $lines += '# Lidarr host assemblies (non-plugin)'
        $lines += 'Lidarr.Core.dll'
        $lines += 'Lidarr.Http.dll'
        $lines += 'Lidarr.Api.V1.dll'
        $lines += 'Lidarr.Common.dll'
        $lines += '# NzbDrone host assemblies'
        $lines += 'NzbDrone.Common.dll'
        $lines += 'NzbDrone.Core.dll'
        $lines += 'NzbDrone.SignalR.dll'
    }
    $lines += ''
    return $lines -join "`n"
}

# --- Execute ---

if ($Check) {
    if (-not $hasExisting) {
        Write-Error "Manifest not found: $ManifestPath (required for -Check mode)"
        exit 1
    }

    $inSync = Show-Report
    if ($inSync) {
        exit 0
    }
    else {
        Write-Host "`nRun with -Update to sync the manifest, or edit manually." -ForegroundColor Yellow
        exit 1
    }
}
elseif ($Update) {
    $text = Get-ManifestText
    $dir = Split-Path -Parent $ManifestPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $text | Set-Content -LiteralPath $ManifestPath -Encoding UTF8 -NoNewline
    # Ensure trailing newline
    Add-Content -LiteralPath $ManifestPath -Value '' -NoNewline:$false
    Write-Host "Updated $ManifestPath ($($requiredFromZip.Count) required, $($existingForbidden.Count -gt 0 ? $existingForbidden.Count : $standardForbidden.Count) forbidden)" -ForegroundColor Green

    if ($violations.Count -gt 0) {
        Write-Host "WARNING: ZIP contains FORBIDDEN files:" -ForegroundColor Red
        $violations | ForEach-Object { Write-Host "  ! $_" -ForegroundColor Red }
        exit 1
    }
}
else {
    # Default: report mode
    Show-Report | Out-Null
    Write-Host ""
    if (-not $hasExisting) {
        Write-Host "Generated manifest:" -ForegroundColor Cyan
        Write-Host (Get-ManifestText)
        Write-Host ""
        Write-Host "Run with -Update to write to $ManifestPath" -ForegroundColor Yellow
    }
}
