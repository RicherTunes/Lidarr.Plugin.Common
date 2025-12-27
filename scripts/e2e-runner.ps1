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
    1. Schema Gate (no provider credentials): Verifies plugin schemas are registered (requires Lidarr API key)
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

.PARAMETER ExtractApiKeyFromContainer
    When set and -ApiKey is not provided, attempts to extract the API key from the running Lidarr Docker container's /config/config.xml.

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

    [switch]$ExtractApiKeyFromContainer,

    [string]$ContainerName = "lidarr-e2e-test",

    [string]$DiagnosticsPath = "./diagnostics",

    [switch]$SkipDiagnostics
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSScriptRoot

# Import modules
Import-Module (Join-Path $PSScriptRoot "lib/e2e-gates.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "lib/e2e-diagnostics.psm1") -Force   

function Get-DockerConfigApiKey {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [int]$TimeoutSeconds = 60
    )

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {        
        throw "docker is required for -ExtractApiKeyFromContainer but was not found in PATH."
    }

    $containerName = $Name.Trim()

    $deadline = (Get-Date).AddSeconds([Math]::Max(1, $TimeoutSeconds))
    while ((Get-Date) -lt $deadline) {
        $configXml = & docker exec $containerName cat /config/config.xml 2>$null
        $configXmlText = (@($configXml) -join "`n")
        if (-not [string]::IsNullOrWhiteSpace($configXmlText)) {
            if ($configXmlText -match '<ApiKey>(?<key>[^<]+)</ApiKey>') {
                $key = $Matches['key'].Trim()
                if (-not [string]::IsNullOrWhiteSpace($key)) {
                    return $key
                }
            }
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out extracting Lidarr API key from container '$containerName'. Ensure the container is running and /config/config.xml exists."
}

function New-OutcomeResult {
    param(
        [Parameter(Mandatory)]
        [string]$Gate,

        [Parameter(Mandatory)]
        [string]$PluginName,

        [Parameter(Mandatory)]
        [ValidateSet("success", "failed", "skipped")]
        [string]$Outcome,

        [string[]]$Errors = @(),

        [hashtable]$Details = @{}
    )

    return [PSCustomObject]@{
        Gate = $Gate
        PluginName = $PluginName
        Outcome = $Outcome
        Success = ($Outcome -eq "success")
        Errors = $Errors
        Details = $Details
    }
}

# Plugin configurations (including search gate settings)
$pluginConfigs = @{
    'Qobuzarr' = @{
        ExpectIndexer = $true
        ExpectDownloadClient = $true
        ExpectImportList = $false
        # Search gate settings
        SearchQuery = "Kind of Blue Miles Davis"
        ExpectedMinResults = 1
        CredentialFieldNames = @("email", "username", "password")
        SkipIndexerTest = $false
    }
    'Tidalarr' = @{
        ExpectIndexer = $true
        ExpectDownloadClient = $true
        ExpectImportList = $false
        # Search gate settings
        SearchQuery = "Kind of Blue Miles Davis"
        ExpectedMinResults = 1
        CredentialFieldNames = @("configPath", "redirectUrl", "oauthRedirectUrl")
        SkipIndexerTest = $false
        CredentialPathField = "configPath"
        CredentialFileRelative = "tidal_tokens.json"
    }
    'Brainarr' = @{
        ExpectIndexer = $false
        ExpectDownloadClient = $false
        ExpectImportList = $true
        # No search for import lists
        SearchQuery = $null
        ExpectedMinResults = 0
        CredentialFieldNames = @()
        SkipIndexerTest = $true
    }
}

# Resolve API key (required for all gates, including schema)
$effectiveApiKey = $ApiKey
if (-not $effectiveApiKey -and $ExtractApiKeyFromContainer) {
    try {
        $effectiveApiKey = Get-DockerConfigApiKey -Name $ContainerName
    }
    catch {
        Write-Host "ERROR: Failed to extract API key from container '$ContainerName': $_" -ForegroundColor Red
        exit 1
    }
}

if (-not $effectiveApiKey) {
    Write-Host "ERROR: Lidarr API key required. Set LIDARR_API_KEY, pass -ApiKey, or use -ExtractApiKeyFromContainer." -ForegroundColor Red
    exit 1
}

$redactionSelfTestPassed = $false
try {
    $redactionSelfTestPassed = [bool](Test-SecretRedaction)
}
catch {
    if (-not $SkipDiagnostics) {
        Write-Host "ERROR: Diagnostics redaction self-test failed: $_" -ForegroundColor Red
        Write-Host "Refusing to run gates until redaction is fixed (or re-run with -SkipDiagnostics)." -ForegroundColor Yellow
        exit 1
    }
}

Initialize-E2EGates -ApiUrl $LidarrUrl -ApiKey $effectiveApiKey

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
$stopNow = $false

$runSearch = ($Gate -eq "search" -or $Gate -eq "all")
$runGrab = ($Gate -eq "grab" -or $Gate -eq "all")

foreach ($plugin in $pluginList) {
    if ($stopNow) { break }
    Write-Host "Testing: $plugin" -ForegroundColor Yellow
    Write-Host "----------------------------------------" -ForegroundColor DarkGray

    $skipGrabForPlugin = $false

    $config = $pluginConfigs[$plugin]
    if (-not $config) {
        Write-Host "  WARNING: Unknown plugin '$plugin', using defaults" -ForegroundColor Yellow
        $config = @{
            ExpectIndexer = $true
            ExpectDownloadClient = $true
            ExpectImportList = $false
        }
    }

    # Gate 1: Schema (always run)
    Write-Host "  [1/3] Schema Gate..." -ForegroundColor Cyan

    $schemaResult = Test-SchemaGate -PluginName $plugin `
        -ExpectIndexer:$config.ExpectIndexer `
        -ExpectDownloadClient:$config.ExpectDownloadClient `
        -ExpectImportList:$config.ExpectImportList

    $schemaOutcome = if ($schemaResult.Success) { "success" } else { "failed" }
    $schemaRecord = New-OutcomeResult -Gate "Schema" -PluginName $plugin -Outcome $schemaOutcome -Errors $schemaResult.Errors -Details @{
        IndexerFound = $schemaResult.IndexerFound
        DownloadClientFound = $schemaResult.DownloadClientFound
        ImportListFound = $schemaResult.ImportListFound
    }
    $allResults += $schemaRecord

    if ($schemaRecord.Success) {
        Write-Host "       PASS" -ForegroundColor Green
        if ($schemaResult.IndexerFound) { Write-Host "       - Indexer schema found" -ForegroundColor DarkGreen }
        if ($schemaResult.DownloadClientFound) { Write-Host "       - DownloadClient schema found" -ForegroundColor DarkGreen }
        if ($schemaResult.ImportListFound) { Write-Host "       - ImportList schema found" -ForegroundColor DarkGreen }
    }
    else {
        Write-Host "       FAIL" -ForegroundColor Red
        foreach ($err in $schemaResult.Errors) {
            Write-Host "       - $err" -ForegroundColor Red
        }
        $overallSuccess = $false
        $stopNow = $true
        break
    }

    # Gate 2: Search (credentials required)
    if ($runSearch) {
        Write-Host "  [2/3] Search Gate..." -ForegroundColor Cyan

        if (-not $config.ExpectIndexer) {
            if ($config.ExpectImportList) {
                Write-Host "       SKIP (import list only; configure provider to run functional gates)" -ForegroundColor DarkGray
            }
            else {
                Write-Host "       SKIP (no indexer expected)" -ForegroundColor DarkGray
            }
            $allResults += New-OutcomeResult -Gate "Search" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "No indexer expected for plugin"
            }
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
                    $allResults += New-OutcomeResult -Gate "Search" -PluginName $plugin -Outcome "skipped" -Errors @("No configured indexer found for $plugin") -Details @{
                        Reason = "No configured indexer found"
                    }
                }
                else {
                    function Get-IndexerFieldValue {
                        param(
                            [AllowNull()]
                            $Indexer,
                            [Parameter(Mandatory)]
                            [string]$Name
                        )

                        if ($null -eq $Indexer) { return $null }
                        $fields = $Indexer.fields
                        if ($null -eq $fields) { return $null }
                        $arr = if ($fields -is [array]) { $fields } else { @($fields) }
                        foreach ($f in $arr) {
                            $fname = if ($f -is [hashtable]) { $f['name'] } else { $f.name }
                            if ([string]::Equals("$fname", $Name, [StringComparison]::OrdinalIgnoreCase)) {
                                if ($f -is [hashtable]) { return $f['value'] }
                                return $f.value
                            }
                        }
                        return $null
                    }

                    # Optional: Credential file probe (Docker-only) to avoid treating "not authenticated yet" as a failure.
                    if ($config.ContainsKey("CredentialFileRelative") -and $config.ContainsKey("CredentialPathField") -and $ContainerName) {
                        try {
                            $probePathField = "$($config.CredentialPathField)"
                            $probeConfigPath = Get-IndexerFieldValue -Indexer $pluginIndexer -Name $probePathField
                            if (-not [string]::IsNullOrWhiteSpace("$probeConfigPath")) {
                                $relative = "$($config.CredentialFileRelative)"
                                $probeFilePath = "$($probeConfigPath.TrimEnd('/'))/$relative"
                                $probeOk = docker exec $ContainerName sh -c "test -s '$probeFilePath' && echo ok" 2>$null
                                if (-not $probeOk) {
                                    Write-Host "       SKIP (credentials file missing: $probeFilePath)" -ForegroundColor DarkGray
                                    $allResults += New-OutcomeResult -Gate "Search" -PluginName $plugin -Outcome "skipped" -Details @{
                                        Reason = "Credentials file missing"
                                        CredentialFile = $probeFilePath
                                    }
                                    $skipGrabForPlugin = $true
                                    continue
                                }
                            }
                        }
                        catch {
                            # If the probe fails (no docker, permissions, etc.), fall back to normal Search gate behavior.
                        }
                    }

                    # Use per-plugin search config
                    $searchQuery = $config.SearchQuery
                    $expectedMin = $config.ExpectedMinResults
                    if (-not $searchQuery) { $searchQuery = "Kind of Blue Miles Davis" }
                    if ($expectedMin -lt 1) { $expectedMin = 1 }

                    $credFieldNames = @()
                    if ($config.ContainsKey("CredentialFieldNames")) {
                        $credFieldNames = @($config.CredentialFieldNames)
                    }
                    $skipIndexerTest = $true
                    if ($config.ContainsKey("SkipIndexerTest")) {
                        $skipIndexerTest = [bool]$config.SkipIndexerTest
                    }

                    $searchResult = Test-SearchGate -IndexerId $pluginIndexer.id `
                        -SearchQuery $searchQuery `
                        -ExpectedMinResults $expectedMin `
                        -CredentialFieldNames $credFieldNames `
                        -SkipIndexerTest:$skipIndexerTest

                    $searchOutcome = if ($searchResult.Outcome) { $searchResult.Outcome } elseif ($searchResult.Success) { "success" } else { "failed" }
                    $allResults += New-OutcomeResult -Gate "Search" -PluginName $plugin -Outcome $searchOutcome -Errors $searchResult.Errors -Details @{
                        IndexerId = $pluginIndexer.id
                        ResultCount = $searchResult.ResultCount
                        SearchQuery = $searchQuery
                        SkipIndexerTest = $skipIndexerTest
                        RawResponse = $searchResult.RawResponse
                        SkipReason = $searchResult.SkipReason
                    }

                    if ($searchOutcome -eq "skipped") {
                        $reason = $searchResult.SkipReason
                        if (-not $reason) { $reason = "Skipped by gate policy" }
                        Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray

                        # If Search was skipped due to missing credentials, downstream functional gates should also skip.
                        if ($Gate -eq "all") { $skipGrabForPlugin = $true }
                    }
                    elseif ($searchResult.Success) {
                        if (-not $skipIndexerTest) {
                            Write-Host "       PASS (indexer/test)" -ForegroundColor Green
                        }
                        else {
                            Write-Host "       PASS ($($searchResult.ResultCount) results for '$searchQuery')" -ForegroundColor Green
                        }
                    }
                    else {
                        Write-Host "       FAIL" -ForegroundColor Red
                        foreach ($err in $searchResult.Errors) {
                            Write-Host "       - $err" -ForegroundColor Red
                        }
                        $overallSuccess = $false
                        $stopNow = $true
                        break
                    }
                }
            }
            catch {
                Write-Host "       ERROR: $_" -ForegroundColor Red
                $allResults += New-OutcomeResult -Gate "Search" -PluginName $plugin -Outcome "failed" -Errors @("Search gate error: $_")
                $overallSuccess = $false
                $stopNow = $true
                break
            }
        }
    }

    # Gate 3: Grab (credentials required)
    if ($runGrab) {
        Write-Host "  [3/3] Grab Gate..." -ForegroundColor Cyan

        if ($skipGrabForPlugin) {
            Write-Host "       SKIP (Search gate skipped due to missing credentials)" -ForegroundColor DarkGray
            $allResults += New-OutcomeResult -Gate "Grab" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "Search gate skipped due to missing credentials"
            }
        }
        elseif (-not $config.ExpectDownloadClient) {
            if ($config.ExpectImportList) {
                Write-Host "       SKIP (import list only; configure provider to run functional gates)" -ForegroundColor DarkGray
            }
            else {
                Write-Host "       SKIP (no download client expected)" -ForegroundColor DarkGray
            }
            $allResults += New-OutcomeResult -Gate "Grab" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "No download client expected for plugin"
            }
        }
        else {
            Write-Host "       SKIP (manual verification required)" -ForegroundColor Yellow
            Write-Host "       (Grab gate requires a specific release GUID)" -ForegroundColor DarkGray
            $allResults += New-OutcomeResult -Gate "Grab" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "Manual verification required (release GUID needed)"
            }
        }
    }

    Write-Host ""
}

# Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$passed = ($allResults | Where-Object { $_.Outcome -eq "success" }).Count
$failed = ($allResults | Where-Object { $_.Outcome -eq "failed" }).Count
$skipped = ($allResults | Where-Object { $_.Outcome -eq "skipped" }).Count
$total = $allResults.Count

Write-Host "Success: $passed / $total" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Yellow" })
Write-Host "Skipped: $skipped" -ForegroundColor DarkGray
if ($failed -gt 0) {
    Write-Host "Failed: $failed" -ForegroundColor Red
}

# Create diagnostics bundle on failure
if (-not $overallSuccess -and -not $SkipDiagnostics) {
    try {
        Write-Host ""
        New-Item -ItemType Directory -Path $DiagnosticsPath -Force | Out-Null
        $bundlePath = New-DiagnosticsBundle `
            -OutputPath $DiagnosticsPath `
            -ContainerName $ContainerName `
            -LidarrApiUrl $LidarrUrl `
            -LidarrApiKey $effectiveApiKey `
            -GateResults $allResults `
            -RequestedGate $Gate `
            -Plugins $pluginList `
            -RunnerArgs @($MyInvocation.Line) `
            -RedactionSelfTestExecuted `
            -RedactionSelfTestPassed:$redactionSelfTestPassed

        Write-Host ""
        Write-Host (Get-FailureSummary -GateResults $allResults) -ForegroundColor Yellow
    }
    catch {
        Write-Host "ERROR: Failed to create diagnostics bundle: $_" -ForegroundColor Red
    }
}

# Exit code
if ($overallSuccess) {
    Write-Host ""
    if ($skipped -gt 0) {
        Write-Host "No gate failures. Some gates were skipped." -ForegroundColor Yellow
    }
    else {
        Write-Host "All gates passed!" -ForegroundColor Green
    }
    exit 0
}
else {
    Write-Host ""
    Write-Host "Some gates failed. See diagnostics bundle for details." -ForegroundColor Red
    exit 1
}
