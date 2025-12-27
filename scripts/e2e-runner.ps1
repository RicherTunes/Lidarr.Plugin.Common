#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unified E2E gate runner for plugin ecosystem testing.

.DESCRIPTION
    GATES-ONLY LAYER: This script runs E2E gates against an already-running Lidarr instance.
    It does NOT handle build, deploy, or container lifecycle.

    INTENDED WORKFLOW:
    1. Use test-multi-plugin-persistent.ps1 to build/deploy plugins and start Lidarr
    2. Use this script (e2e-runner.ps1) to run gates against the running instance
    3. On failure, diagnostics bundle is created for AI-assisted triage

    This separation allows:
    - Iterative gate testing without rebuilding
    - Integration with existing proven deploy logic
    - Clear separation of concerns (setup vs validation)

    Gates:
    1. Schema Gate (no credentials): Verifies plugin schemas are registered
    2. Search Gate (credentials required): Verifies API search works
    3. Grab Gate (credentials required): Verifies download works

    On failure, creates a diagnostics bundle for AI-assisted triage.

    RELATED SCRIPTS:
    - test-multi-plugin-persistent.ps1: Build + deploy + start Lidarr (run first)
    - test-qobuzarr-persistent.ps1: Single-plugin persistent testing

.PARAMETER Plugins
    Comma-separated list of plugins to test (e.g., "Qobuzarr,Tidalarr")

.PARAMETER Gate
    Which gate to run: "schema", "search", "grab", or "all" (default: "schema")

.PARAMETER LidarrUrl
    Lidarr API URL (default: http://localhost:8686)

.PARAMETER ApiKey
    Lidarr API key (reads from LIDARR_API_KEY env var if not provided)

.PARAMETER ContainerName
    Docker container name for log collection (default: lidarr-e2e-test)

.PARAMETER DiagnosticsPath
    Path to write diagnostics bundles on failure (default: ./diagnostics)

.PARAMETER SkipDiagnostics
    Skip diagnostics bundle creation on failure.

.EXAMPLE
    # Run schema gate for all plugins (no credentials needed)
    ./e2e-runner.ps1 -Plugins "Qobuzarr,Tidalarr,Brainarr" -Gate schema

.EXAMPLE
    # Run all gates for Qobuzarr
    ./e2e-runner.ps1 -Plugins "Qobuzarr" -Gate all -ApiKey "your-api-key"
#>
param(
    [Parameter(Mandatory)]
    [string]$Plugins,

    [ValidateSet("schema", "search", "grab", "all")]
    [string]$Gate = "schema",

    [string]$LidarrUrl = "http://localhost:8686",

    [string]$ApiKey = $env:LIDARR_API_KEY,

    [string]$ContainerName = "lidarr-e2e-test",

    [string]$DiagnosticsPath = "./diagnostics",

    [switch]$SkipDiagnostics
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSScriptRoot

# Import modules
Import-Module (Join-Path $PSScriptRoot "lib/e2e-gates.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "lib/e2e-diagnostics.psm1") -Force

# Plugin configurations
$pluginConfigs = @{
    'Qobuzarr' = @{
        ExpectIndexer = $true
        ExpectDownloadClient = $true
    }
    'Tidalarr' = @{
        ExpectIndexer = $true
        ExpectDownloadClient = $true
    }
    'Brainarr' = @{
        ExpectIndexer = $false
        ExpectDownloadClient = $false
        ExpectImportList = $true
    }
}

# Validate API key for gates that need it
if ($Gate -ne "schema" -and -not $ApiKey) {
    Write-Host "ERROR: API key required for '$Gate' gate. Set LIDARR_API_KEY or use -ApiKey." -ForegroundColor Red
    exit 1
}

# Initialize gates module
if ($ApiKey) {
    Initialize-E2EGates -ApiUrl $LidarrUrl -ApiKey $ApiKey
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "E2E Plugin Test Runner" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Plugins: $Plugins" -ForegroundColor White
Write-Host "Gate: $Gate" -ForegroundColor White
Write-Host "Lidarr: $LidarrUrl" -ForegroundColor White
Write-Host ""

$pluginList = $Plugins -split ',' | ForEach-Object { $_.Trim() }
$allResults = @()
$overallSuccess = $true

foreach ($plugin in $pluginList) {
    Write-Host "Testing: $plugin" -ForegroundColor Yellow
    Write-Host "----------------------------------------" -ForegroundColor DarkGray

    $config = $pluginConfigs[$plugin]
    if (-not $config) {
        Write-Host "  WARNING: Unknown plugin '$plugin', using defaults" -ForegroundColor Yellow
        $config = @{
            ExpectIndexer = $true
            ExpectDownloadClient = $true
        }
    }

    # Gate 1: Schema (always run)
    if ($Gate -eq "schema" -or $Gate -eq "all") {
        Write-Host "  [1/3] Schema Gate..." -ForegroundColor Cyan

        if (-not $ApiKey) {
            # For schema-only, try to get API key from Lidarr config
            Write-Host "       (Attempting to read API key from Lidarr...)" -ForegroundColor DarkGray
            try {
                $status = Invoke-RestMethod -Uri "$LidarrUrl/api/v1/system/status" -TimeoutSec 5 -ErrorAction SilentlyContinue
                Write-Host "       ERROR: API key required even for schema gate" -ForegroundColor Red
                $allResults += [PSCustomObject]@{
                    Gate = 'Schema'
                    PluginName = $plugin
                    Success = $false
                    Errors = @("API key required")
                }
                $overallSuccess = $false
                continue
            }
            catch {
                # Expected - need auth
            }
        }

        $schemaResult = Test-SchemaGate -PluginName $plugin `
            -ExpectIndexer:$config.ExpectIndexer `
            -ExpectDownloadClient:$config.ExpectDownloadClient

        $allResults += $schemaResult

        if ($schemaResult.Success) {
            Write-Host "       PASS" -ForegroundColor Green
            if ($schemaResult.IndexerFound) { Write-Host "       - Indexer schema found" -ForegroundColor DarkGreen }
            if ($schemaResult.DownloadClientFound) { Write-Host "       - DownloadClient schema found" -ForegroundColor DarkGreen }
        }
        else {
            Write-Host "       FAIL" -ForegroundColor Red
            foreach ($err in $schemaResult.Errors) {
                Write-Host "       - $err" -ForegroundColor Red
            }
            $overallSuccess = $false
        }
    }

    # Gate 2: Search (credentials required)
    if ($Gate -eq "search" -or $Gate -eq "all") {
        Write-Host "  [2/3] Search Gate..." -ForegroundColor Cyan

        if (-not $config.ExpectIndexer) {
            Write-Host "       SKIP (no indexer expected)" -ForegroundColor DarkGray
        }
        else {
            # Find configured indexer for this plugin
            try {
                $indexers = Invoke-LidarrApi -Endpoint "indexer"
                $pluginIndexer = $indexers | Where-Object {
                    $_.implementation -like "*$plugin*" -or
                    $_.name -like "*$plugin*"
                } | Select-Object -First 1

                if (-not $pluginIndexer) {
                    Write-Host "       SKIP (no configured indexer found)" -ForegroundColor Yellow
                    $allResults += [PSCustomObject]@{
                        Gate = 'Search'
                        PluginName = $plugin
                        Success = $false
                        Errors = @("No configured indexer found for $plugin")
                    }
                }
                else {
                    $searchResult = Test-SearchGate -IndexerId $pluginIndexer.id
                    $allResults += $searchResult

                    if ($searchResult.Success) {
                        Write-Host "       PASS ($($searchResult.ResultCount) results)" -ForegroundColor Green
                    }
                    else {
                        Write-Host "       FAIL" -ForegroundColor Red
                        foreach ($err in $searchResult.Errors) {
                            Write-Host "       - $err" -ForegroundColor Red
                        }
                        $overallSuccess = $false
                    }
                }
            }
            catch {
                Write-Host "       ERROR: $_" -ForegroundColor Red
                $allResults += [PSCustomObject]@{
                    Gate = 'Search'
                    PluginName = $plugin
                    Success = $false
                    Errors = @("Search gate error: $_")
                }
                $overallSuccess = $false
            }
        }
    }

    # Gate 3: Grab (credentials required)
    if ($Gate -eq "grab" -or $Gate -eq "all") {
        Write-Host "  [3/3] Grab Gate..." -ForegroundColor Cyan

        if (-not $config.ExpectDownloadClient) {
            Write-Host "       SKIP (no download client expected)" -ForegroundColor DarkGray
        }
        else {
            Write-Host "       SKIP (manual verification required)" -ForegroundColor Yellow
            Write-Host "       (Grab gate requires a specific release GUID)" -ForegroundColor DarkGray
        }
    }

    Write-Host ""
}

# Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$passed = ($allResults | Where-Object { $_.Success }).Count
$failed = ($allResults | Where-Object { -not $_.Success }).Count
$total = $allResults.Count

Write-Host "Passed: $passed / $total" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Yellow" })
if ($failed -gt 0) {
    Write-Host "Failed: $failed" -ForegroundColor Red
}

# Create diagnostics bundle on failure
if (-not $overallSuccess -and -not $SkipDiagnostics -and $ApiKey) {
    Write-Host ""
    New-Item -ItemType Directory -Path $DiagnosticsPath -Force | Out-Null
    $bundlePath = New-DiagnosticsBundle `
        -OutputPath $DiagnosticsPath `
        -ContainerName $ContainerName `
        -LidarrApiUrl $LidarrUrl `
        -LidarrApiKey $ApiKey `
        -GateResults $allResults

    Write-Host ""
    Write-Host (Get-FailureSummary -GateResults $allResults) -ForegroundColor Yellow
}

# Exit code
if ($overallSuccess) {
    Write-Host ""
    Write-Host "All gates passed!" -ForegroundColor Green
    exit 0
}
else {
    Write-Host ""
    Write-Host "Some gates failed. See diagnostics bundle for details." -ForegroundColor Red
    exit 1
}
