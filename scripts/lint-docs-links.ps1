<#
.SYNOPSIS
    Verify required documentation links exist in plugin repositories.

.DESCRIPTION
    This script checks that plugin repositories contain required references to
    canonical documentation in the Common library (ADRs, support matrix, etc.).

    Required links are defined in the schema file or passed as parameters.

.PARAMETER PluginRoot
    Root directory of the plugin repository. Defaults to current directory.

.PARAMETER CommonPath
    Path to Common submodule. Defaults to ext/Lidarr.Plugin.Common

.PARAMETER SchemaFile
    Path to required links schema. Defaults to Common's docs/schemas/required-links.json

.NOTES
    CANONICAL LOCATION: ext/Lidarr.Plugin.Common/scripts/lint-docs-links.ps1
    Do NOT copy this file - always reference from Common submodule.

    Schema format (docs/schemas/required-links.json):
    {
      "version": "1.0",
      "required_links": [
        {
          "target": "docs/decisions/ADR-001-streaming-architecture.md",
          "description": "Streaming architecture decision",
          "search_patterns": ["ADR-001", "streaming-architecture"]
        }
      ],
      "search_locations": ["README.md", "CLAUDE.md", "docs/**/*.md"]
    }
#>

param(
    [string]$PluginRoot = ".",
    [string]$CommonPath = "ext/Lidarr.Plugin.Common",
    [string]$SchemaFile = "",
    [switch]$CI
)

$ErrorActionPreference = "Stop"

# Resolve paths
$PluginRoot = Resolve-Path $PluginRoot -ErrorAction Stop
$CommonFullPath = Join-Path $PluginRoot $CommonPath

if (-not (Test-Path $CommonFullPath)) {
    Write-Host "::error::Common submodule not found at $CommonFullPath" -ForegroundColor Red
    if ($CI) { exit 1 }
    return $false
}

# Load schema - use embedded defaults if no schema file
$defaultSchema = @{
    version = "1.0"
    required_links = @(
        @{
            target = "docs/decisions/ADR-001-streaming-architecture.md"
            description = "Streaming architecture decision (CLI-only)"
            search_patterns = @("ADR-001", "streaming-architecture", "STREAMING_SUPPORT")
        }
        @{
            target = "docs/decisions/ADR-002-subscription-auth-research.md"
            description = "Subscription auth research decision"
            search_patterns = @("ADR-002", "subscription-auth", "auth-research")
        }
        @{
            target = "docs/STREAMING_SUPPORT.md"
            description = "Streaming support matrix"
            search_patterns = @("STREAMING_SUPPORT", "streaming support", "decoder")
        }
    )
    search_locations = @("README.md", "CLAUDE.md", "docs/**/*.md", ".planning/**/*.md")
}

$schema = $defaultSchema
if ($SchemaFile -and (Test-Path $SchemaFile)) {
    $schema = Get-Content $SchemaFile -Raw | ConvertFrom-Json
} elseif (-not $SchemaFile) {
    $schemaPath = Join-Path $CommonFullPath "docs/schemas/required-links.json"
    if (Test-Path $schemaPath) {
        $schema = Get-Content $schemaPath -Raw | ConvertFrom-Json
    }
}

# Verify required docs exist in Common
$missingInCommon = @()
foreach ($link in $schema.required_links) {
    $targetPath = Join-Path $CommonFullPath $link.target
    if (-not (Test-Path $targetPath)) {
        $missingInCommon += $link.target
    }
}

if ($missingInCommon.Count -gt 0) {
    Write-Host "::warning::Required docs missing in Common:" -ForegroundColor Yellow
    foreach ($m in $missingInCommon) {
        Write-Host "  - $m" -ForegroundColor Yellow
    }
}

# Collect all searchable files in plugin
$searchFiles = @()
foreach ($pattern in $schema.search_locations) {
    $fullPattern = Join-Path $PluginRoot $pattern
    $matches = Get-ChildItem -Path $fullPattern -ErrorAction SilentlyContinue
    $searchFiles += $matches
}

if ($searchFiles.Count -eq 0) {
    Write-Host "::warning::No documentation files found to search" -ForegroundColor Yellow
    Write-Host "  Searched patterns: $($schema.search_locations -join ', ')"
}

# Check for required links
$foundLinks = @{}
$missingLinks = @()

foreach ($link in $schema.required_links) {
    $found = $false
    $foundIn = @()

    foreach ($file in $searchFiles) {
        if (-not (Test-Path $file.FullName)) { continue }
        $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $content) { continue }

        foreach ($pattern in $link.search_patterns) {
            if ($content -match [regex]::Escape($pattern)) {
                $found = $true
                $foundIn += $file.Name
                break
            }
        }
    }

    if ($found) {
        $foundLinks[$link.target] = $foundIn | Select-Object -Unique
    } else {
        $missingLinks += $link
    }
}

# Report results
Write-Host ""
Write-Host "=== Docs Drift Sentinel Report ===" -ForegroundColor Cyan
Write-Host ""

if ($foundLinks.Count -gt 0) {
    Write-Host "[FOUND] Required documentation references:" -ForegroundColor Green
    foreach ($key in $foundLinks.Keys) {
        $files = $foundLinks[$key] -join ", "
        Write-Host "  + $key" -ForegroundColor Green
        Write-Host "    Referenced in: $files" -ForegroundColor Gray
    }
}

if ($missingLinks.Count -gt 0) {
    Write-Host ""
    Write-Host "[MISSING] Required documentation references:" -ForegroundColor Red
    foreach ($link in $missingLinks) {
        Write-Host "  - $($link.target)" -ForegroundColor Red
        Write-Host "    $($link.description)" -ForegroundColor Gray
        Write-Host "    Search patterns: $($link.search_patterns -join ', ')" -ForegroundColor Gray
    }
    Write-Host ""
    Write-Host "To fix: Reference these docs in README.md, CLAUDE.md, or docs/*.md" -ForegroundColor Cyan
    Write-Host "Example: See [ADR-001](ext/Lidarr.Plugin.Common/docs/decisions/ADR-001-streaming-architecture.md)" -ForegroundColor Cyan
    Write-Host ""

    if ($CI) { exit 1 }
    return $false
}

Write-Host ""
Write-Host "[OK] All required documentation references found" -ForegroundColor Green
return $true
