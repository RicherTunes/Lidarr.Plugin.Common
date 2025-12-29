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
    1. Schema Gate (requires Lidarr API key): Verifies plugin schemas are registered
    2. Configure Gate (optional): Fixes known configuration drift (e.g., OAuth split-brain)
    3. Search Gate (credentials required): Verifies indexer/test passes
    4. AlbumSearch Gate (credentials required): Triggers AlbumSearch command, verifies releases from plugin
    5. Grab Gate (credentials required): Verifies download works
    6. Persist Gate (optional): Restarts container and verifies configured components persist

    Combined:
    - bootstrap: configure + all + persist

    On failure, creates a diagnostics bundle for AI-assisted triage.

    RELATED SCRIPTS:
    - test-multi-plugin-persistent.ps1: Build + deploy + start Lidarr (run first)
    - test-qobuzarr-persistent.ps1: Single-plugin persistent testing

.PARAMETER Plugins
    Comma-separated list of plugins to test (e.g., "Qobuzarr,Tidalarr")

.PARAMETER Gate
    Which gate to run: "schema", "configure", "search", "albumsearch", "grab", "all", "persist", or "bootstrap" (default: "schema")

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

    [ValidateSet("schema", "configure", "search", "albumsearch", "grab", "all", "persist", "bootstrap")]
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

function Get-LidarrFieldValue {
    param(
        [AllowNull()]
        $Fields,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $Fields) { return $null }
    $arr = if ($Fields -is [array]) { $Fields } else { @($Fields) }
    foreach ($f in $arr) {
        $fname = if ($f -is [hashtable]) { $f['name'] } else { $f.name }
        if ([string]::Equals("$fname", $Name, [StringComparison]::OrdinalIgnoreCase)) {
            if ($f -is [hashtable]) { return $f['value'] }
            return $f.value
        }
    }
    return $null
}

function Set-LidarrFieldValue {
    param(
        [AllowNull()]
        $Fields,
        [Parameter(Mandatory)]
        [string]$Name,
        [AllowNull()]
        $Value
    )

    if ($null -eq $Fields) { return @() }
    $arr = if ($Fields -is [array]) { @($Fields) } else { @($Fields) }

    $updated = $false
    foreach ($f in $arr) {
        $fname = if ($f -is [hashtable]) { $f['name'] } else { $f.name }
        if ([string]::Equals("$fname", $Name, [StringComparison]::OrdinalIgnoreCase)) {
            if ($f -is [hashtable]) { $f['value'] = $Value } else { $f.value = $Value }
            $updated = $true
            break
        }
    }

    if (-not $updated) {
        $arr += [PSCustomObject]@{ name = $Name; value = $Value }
    }

    return $arr
}

function Find-ConfiguredComponent {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("indexer", "downloadclient", "importlist")]
        [string]$Type,

        [Parameter(Mandatory)]
        [string]$PluginName
    )

    $endpoint = switch ($Type) {
        "indexer" { "indexer" }
        "downloadclient" { "downloadclient" }
        "importlist" { "importlist" }
    }

    $items = Invoke-LidarrApi -Endpoint $endpoint
    if (-not $items) { return $null }

    return $items | Where-Object {
        $_.implementation -like "*$PluginName*" -or
        $_.name -like "*$PluginName*"
    } | Select-Object -First 1
}

function Test-ConfigureGateForPlugin {
    param(
        [Parameter(Mandatory)]
        [string]$PluginName,

        [hashtable]$PluginConfig = @{}
    )

    $result = [PSCustomObject]@{
        Gate = "Configure"
        PluginName = $PluginName
        Outcome = "skipped" # success|failed|skipped
        Success = $false
        Actions = @()
        Errors = @()
        SkipReason = $null
    }

    # Currently Configure gate is intentionally conservative: it only syncs known
    # “split-brain” configuration that causes real E2E failures (e.g., OAuth configured
    # on an indexer but missing on its paired download client).
    if ($PluginName -ne "Tidalarr") {
        $result.Outcome = "success"
        $result.Success = $true
        return $result
    }

    try {
        $indexer = Find-ConfiguredComponent -Type "indexer" -PluginName $PluginName
        $client = Find-ConfiguredComponent -Type "downloadclient" -PluginName $PluginName

        if (-not $indexer) {
            $result.SkipReason = "No configured indexer found"
            return $result
        }
        if (-not $client) {
            $result.SkipReason = "No configured download client found"
            return $result
        }

        $indexerFull = Invoke-LidarrApi -Endpoint ("indexer/{0}" -f $indexer.id)
        $clientFull = Invoke-LidarrApi -Endpoint ("downloadclient/{0}" -f $client.id)

        $indexerConfigPath = Get-LidarrFieldValue -Fields $indexerFull.fields -Name "configPath"
        $clientConfigPath = Get-LidarrFieldValue -Fields $clientFull.fields -Name "configPath"

        $indexerRedirectUrl = Get-LidarrFieldValue -Fields $indexerFull.fields -Name "redirectUrl"
        if ([string]::IsNullOrWhiteSpace("$indexerRedirectUrl")) {
            $indexerRedirectUrl = Get-LidarrFieldValue -Fields $indexerFull.fields -Name "oauthRedirectUrl"
        }

        $clientRedirectUrl = Get-LidarrFieldValue -Fields $clientFull.fields -Name "redirectUrl"
        if ([string]::IsNullOrWhiteSpace("$clientRedirectUrl")) {
            $clientRedirectUrl = Get-LidarrFieldValue -Fields $clientFull.fields -Name "oauthRedirectUrl"
        }

        $needsUpdate = $false
        $updatedFields = @($clientFull.fields)

        if (-not [string]::IsNullOrWhiteSpace("$indexerConfigPath") -and [string]::IsNullOrWhiteSpace("$clientConfigPath")) {
            $updatedFields = Set-LidarrFieldValue -Fields $updatedFields -Name "configPath" -Value $indexerConfigPath
            $result.Actions += "Copied configPath from indexer to download client"
            $needsUpdate = $true
        }

        if (-not [string]::IsNullOrWhiteSpace("$indexerRedirectUrl") -and [string]::IsNullOrWhiteSpace("$clientRedirectUrl")) {
            # Do not log the URL content (contains auth codes).
            $updatedFields = Set-LidarrFieldValue -Fields $updatedFields -Name "redirectUrl" -Value $indexerRedirectUrl
            $result.Actions += "Copied redirectUrl from indexer to download client"
            $needsUpdate = $true
        }

        if (-not $needsUpdate) {
            $result.Outcome = "success"
            $result.Success = $true
            return $result
        }

        $clientFull.fields = $updatedFields

        try {
            Invoke-LidarrApi -Endpoint ("downloadclient/{0}" -f $clientFull.id) -Method PUT -Body $clientFull | Out-Null
        }
        catch {
            # Fallback: some Lidarr versions accept PUT /downloadclient with body containing id.
            Invoke-LidarrApi -Endpoint "downloadclient" -Method PUT -Body $clientFull | Out-Null
        }

        $result.Outcome = "success"
        $result.Success = $true
        return $result
    }
    catch {
        $result.Outcome = "failed"
        $result.Success = $false
        $result.Errors += "Configure gate failed: $_"
        return $result
    }
}

function Wait-LidarrReady {
    param(
        [Parameter(Mandatory)]
        [string]$BaseUrl,
        [Parameter(Mandatory)]
        [string]$ApiKey,
        [int]$TimeoutSec = 120
    )

    $deadline = (Get-Date).AddSeconds([Math]::Max(5, $TimeoutSec))
    while ((Get-Date) -lt $deadline) {
        try {
            $headers = @{ "X-Api-Key" = $ApiKey }
            $status = Invoke-RestMethod -Uri ("{0}/api/v1/system/status" -f $BaseUrl.TrimEnd('/')) -Headers $headers -TimeoutSec 10
            if ($status) { return $true }
        }
        catch { }
        Start-Sleep -Milliseconds 750
    }

    return $false
}

function Test-PersistGate {
    param(
        [Parameter(Mandatory)]
        [string[]]$PluginList,
        [Parameter(Mandatory)]
        [hashtable]$PluginConfigs,
        [Parameter(Mandatory)]
        [string]$LidarrUrl,
        [Parameter(Mandatory)]
        [string]$ApiKey,
        [Parameter(Mandatory)]
        [string]$ContainerName
    )

    $result = [PSCustomObject]@{
        Gate = "Persist"
        PluginName = "Ecosystem"
        Outcome = "skipped"
        Success = $false
        Errors = @()
        Details = @{}
    }

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        $result.Outcome = "skipped"
        $result.Details = @{ Reason = "docker not found" }
        return $result
    }

    if ([string]::IsNullOrWhiteSpace($ContainerName)) {
        $result.Outcome = "skipped"
        $result.Details = @{ Reason = "ContainerName not provided" }
        return $result
    }

    # Capture baseline configuration presence (by IDs) before restart.
    $baseline = @{}
    foreach ($plugin in $PluginList) {
        $cfg = $PluginConfigs[$plugin]
        if (-not $cfg) { continue }

        $baseline[$plugin] = @{
            IndexerId = $null
            DownloadClientId = $null
            ImportListId = $null
        }

        try {
            if ($cfg.ExpectIndexer) {
                $idx = Find-ConfiguredComponent -Type "indexer" -PluginName $plugin
                if ($idx) { $baseline[$plugin].IndexerId = $idx.id }
            }
            if ($cfg.ExpectDownloadClient) {
                $dc = Find-ConfiguredComponent -Type "downloadclient" -PluginName $plugin
                if ($dc) { $baseline[$plugin].DownloadClientId = $dc.id }
            }
            if ($cfg.ExpectImportList) {
                $il = Find-ConfiguredComponent -Type "importlist" -PluginName $plugin
                if ($il) { $baseline[$plugin].ImportListId = $il.id }
            }
        }
        catch { }
    }

    # Restart container.
    try {
        & docker restart $ContainerName | Out-Null
    }
    catch {
        $result.Outcome = "failed"
        $result.Errors += "Failed to restart container '$ContainerName': $_"
        return $result
    }

    if (-not (Wait-LidarrReady -BaseUrl $LidarrUrl -ApiKey $ApiKey -TimeoutSec 180)) {
        $result.Outcome = "failed"
        $result.Errors += "Timed out waiting for Lidarr API after restart"
        return $result
    }

    # Verify configuration still present.
    $failures = @()
    foreach ($plugin in $PluginList) {
        if (-not $baseline.ContainsKey($plugin)) { continue }
        $b = $baseline[$plugin]

        if ($null -ne $b.IndexerId) {
            try {
                $idxAfter = Invoke-LidarrApi -Endpoint ("indexer/{0}" -f $b.IndexerId)
                if (-not $idxAfter) { $failures += "$plugin indexer missing after restart (id=$($b.IndexerId))" }
            }
            catch { $failures += "$plugin indexer missing after restart (id=$($b.IndexerId))" }
        }
        if ($null -ne $b.DownloadClientId) {
            try {
                $dcAfter = Invoke-LidarrApi -Endpoint ("downloadclient/{0}" -f $b.DownloadClientId)
                if (-not $dcAfter) { $failures += "$plugin download client missing after restart (id=$($b.DownloadClientId))" }
            }
            catch { $failures += "$plugin download client missing after restart (id=$($b.DownloadClientId))" }
        }
        if ($null -ne $b.ImportListId) {
            try {
                $ilAfter = Invoke-LidarrApi -Endpoint ("importlist/{0}" -f $b.ImportListId)
                if (-not $ilAfter) { $failures += "$plugin import list missing after restart (id=$($b.ImportListId))" }
            }
            catch { $failures += "$plugin import list missing after restart (id=$($b.ImportListId))" }
        }
    }

    $result.Details = @{ Baseline = $baseline }

    if ($failures.Count -gt 0) {
        $result.Outcome = "failed"
        $result.Errors = $failures
        return $result
    }

    $result.Outcome = "success"
    $result.Success = $true
    return $result
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
        # Qobuzarr supports two auth modes:
        # 1. Email/Password: (email OR username) + password
        # 2. Token: userId + authToken
        CredentialAllOfFieldNames = @()
        CredentialAnyOfFieldNames = @(
            @("password", "email"),
            @("password", "username"),
            @("authToken", "userId")
        )
        SkipIndexerTest = $false
    }
    'Tidalarr' = @{
        ExpectIndexer = $true
        ExpectDownloadClient = $true
        ExpectImportList = $false
        # Search gate settings
        SearchQuery = "Kind of Blue Miles Davis"
        ExpectedMinResults = 1
        CredentialAllOfFieldNames = @("configPath")
        CredentialAnyOfFieldNames = @("redirectUrl", "oauthRedirectUrl")
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
        CredentialAllOfFieldNames = @()
        CredentialAnyOfFieldNames = @()
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

$runConfigure = ($Gate -eq "configure" -or $Gate -eq "bootstrap")
$runSearch = ($Gate -eq "search" -or $Gate -eq "all" -or $Gate -eq "bootstrap")
$runAlbumSearch = ($Gate -eq "albumsearch" -or $Gate -eq "all" -or $Gate -eq "bootstrap")
$runGrab = ($Gate -eq "grab" -or $Gate -eq "all" -or $Gate -eq "bootstrap")
$runPersist = ($Gate -eq "persist" -or $Gate -eq "bootstrap")

foreach ($plugin in $pluginList) {
    if ($stopNow) { break }
    Write-Host "Testing: $plugin" -ForegroundColor Yellow
    Write-Host "----------------------------------------" -ForegroundColor DarkGray

    $skipGrabForPlugin = $false
    $lastAlbumSearchResult = $null  # Used to pass AlbumId to Grab gate
    $lastPluginIndexer = $null      # Used to pass IndexerId to Grab gate

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
    Write-Host "  [1/4] Schema Gate..." -ForegroundColor Cyan

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

    if ($runConfigure) {
        Write-Host "  Configure Gate..." -ForegroundColor Cyan

        $configureResult = Test-ConfigureGateForPlugin -PluginName $plugin -PluginConfig $config
        $allResults += New-OutcomeResult -Gate "Configure" -PluginName $plugin -Outcome $configureResult.Outcome -Errors $configureResult.Errors -Details @{
            Actions = $configureResult.Actions
            SkipReason = $configureResult.SkipReason
        }

        if ($configureResult.Outcome -eq "skipped") {
            $reason = $configureResult.SkipReason
            if (-not $reason) { $reason = "Skipped by gate policy" }
            Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray
        }
        elseif ($configureResult.Success) {
            if ($configureResult.Actions -and $configureResult.Actions.Count -gt 0) {
                Write-Host "       PASS ($($configureResult.Actions.Count) action(s))" -ForegroundColor Green
            }
            else {
                Write-Host "       PASS" -ForegroundColor Green
            }
        }
        else {
            Write-Host "       FAIL" -ForegroundColor Red
            foreach ($err in $configureResult.Errors) {
                Write-Host "       - $err" -ForegroundColor Red
            }
            $overallSuccess = $false
            $stopNow = $true
            break
        }
    }

    # Gate 2: Search (credentials required) - quick indexer/test validation
    if ($runSearch) {
        Write-Host "  [2/4] Search Gate..." -ForegroundColor Cyan

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

                    $credAllOf = @()
                    $credAnyOf = @()
                    if ($config.ContainsKey("CredentialAllOfFieldNames")) {
                        $credAllOf = @($config.CredentialAllOfFieldNames)
                    }
                    if ($config.ContainsKey("CredentialAnyOfFieldNames")) {
                        $credAnyOf = @($config.CredentialAnyOfFieldNames)
                    }
                    # Backward compatibility
                    if ($config.ContainsKey("CredentialFieldNames")) {
                        $credAllOf = @($config.CredentialFieldNames)
                    }
                    $skipIndexerTest = $true
                    if ($config.ContainsKey("SkipIndexerTest")) {
                        $skipIndexerTest = [bool]$config.SkipIndexerTest
                    }

                    $searchResult = Test-SearchGate -IndexerId $pluginIndexer.id `
                        -SearchQuery $searchQuery `
                        -ExpectedMinResults $expectedMin `
                        -CredentialAllOfFieldNames $credAllOf `
                        -CredentialAnyOfFieldNames $credAnyOf `
                        -SkipIndexerTest:$skipIndexerTest

                    $searchOutcome = if ($searchResult.Outcome) { $searchResult.Outcome } elseif ($searchResult.Success) { "success" } else { "failed" }
                    $allResults += New-OutcomeResult -Gate "Search" -PluginName $plugin -Outcome $searchOutcome -Errors $searchResult.Errors -Details @{
                        IndexerId = $pluginIndexer.id
                        ResultCount = $searchResult.ResultCount
                        SearchQuery = $searchQuery
                        SkipIndexerTest = $skipIndexerTest
                        CredentialAllOf = $credAllOf
                        CredentialAnyOf = $credAnyOf
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

    # Gate 3: AlbumSearch (credentials required) - thorough search verification
    if ($runAlbumSearch) {
        Write-Host "  [3/4] AlbumSearch Gate..." -ForegroundColor Cyan

        if ($skipGrabForPlugin) {
            Write-Host "       SKIP (Search gate skipped due to missing credentials)" -ForegroundColor DarkGray
            $allResults += New-OutcomeResult -Gate "AlbumSearch" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "Search gate skipped due to missing credentials"
            }
        }
        elseif (-not $config.ExpectIndexer) {
            if ($config.ExpectImportList) {
                Write-Host "       SKIP (import list only)" -ForegroundColor DarkGray
            }
            else {
                Write-Host "       SKIP (no indexer expected)" -ForegroundColor DarkGray
            }
            $allResults += New-OutcomeResult -Gate "AlbumSearch" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "No indexer expected for plugin"
            }
        }
        else {
            try {
                # Find configured indexer for this plugin
                $indexers = Invoke-LidarrApi -Endpoint "indexer"
                $pluginIndexer = $indexers | Where-Object {
                    $_.implementation -like "*$plugin*" -or
                    $_.name -like "*$plugin*"
                } | Select-Object -First 1

                if (-not $pluginIndexer) {
                    Write-Host "       SKIP (no configured indexer found)" -ForegroundColor Yellow
                    $allResults += New-OutcomeResult -Gate "AlbumSearch" -PluginName $plugin -Outcome "skipped" -Errors @("No configured indexer found for $plugin")
                }
                else {
                    $credAllOf = @()
                    $credAnyOf = @()
                    if ($config.ContainsKey("CredentialAllOfFieldNames")) {
                        $credAllOf = @($config.CredentialAllOfFieldNames)
                    }
                    if ($config.ContainsKey("CredentialAnyOfFieldNames")) {
                        $credAnyOf = @($config.CredentialAnyOfFieldNames)
                    }
                    # Backward compatibility
                    if ($config.ContainsKey("CredentialFieldNames")) {
                        $credAllOf = @($config.CredentialFieldNames)
                    }

                    # Use per-plugin test artist/album or defaults
                    $testArtist = if ($config.ContainsKey("TestArtistName")) { $config.TestArtistName } else { "Miles Davis" }
                    $testAlbum = if ($config.ContainsKey("TestAlbumName")) { $config.TestAlbumName } else { "Kind of Blue" }

                    $albumSearchResult = Test-AlbumSearchGate -IndexerId $pluginIndexer.id `
                        -PluginName $plugin `
                        -TestArtistName $testArtist `
                        -TestAlbumName $testAlbum `
                        -CredentialAllOfFieldNames $credAllOf `
                        -CredentialAnyOfFieldNames $credAnyOf `
                        -SkipIfNoCreds:$true

                    # Store for Grab gate
                    $lastAlbumSearchResult = $albumSearchResult
                    $lastPluginIndexer = $pluginIndexer

                    $outcome = if ($albumSearchResult.Outcome) { $albumSearchResult.Outcome } elseif ($albumSearchResult.Success) { "success" } else { "failed" }
                    $allResults += New-OutcomeResult -Gate "AlbumSearch" -PluginName $plugin -Outcome $outcome -Errors $albumSearchResult.Errors -Details @{
                        IndexerId = $pluginIndexer.id
                        ArtistId = $albumSearchResult.ArtistId
                        AlbumId = $albumSearchResult.AlbumId
                        CommandId = $albumSearchResult.CommandId
                        ReleaseCount = $albumSearchResult.ReleaseCount    
                        PluginReleaseCount = $albumSearchResult.PluginReleaseCount
                        CredentialAllOf = $credAllOf
                        CredentialAnyOf = $credAnyOf
                        SkipReason = $albumSearchResult.SkipReason        
                    }

                    if ($outcome -eq "skipped") {
                        $reason = $albumSearchResult.SkipReason
                        if (-not $reason) { $reason = "Skipped by gate policy" }
                        Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray
                        $skipGrabForPlugin = $true
                    }
                    elseif ($albumSearchResult.Success) {
                        Write-Host "       PASS ($($albumSearchResult.PluginReleaseCount) releases from $plugin)" -ForegroundColor Green
                    }
                    else {
                        Write-Host "       FAIL" -ForegroundColor Red
                        foreach ($err in $albumSearchResult.Errors) {
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
                $allResults += New-OutcomeResult -Gate "AlbumSearch" -PluginName $plugin -Outcome "failed" -Errors @("AlbumSearch gate error: $_")
                $overallSuccess = $false
                $stopNow = $true
                break
            }
        }
    }

    # Gate 4: Grab (credentials required)
    if ($runGrab) {
        Write-Host "  [4/4] Grab Gate..." -ForegroundColor Cyan

        if ($skipGrabForPlugin) {
            Write-Host "       SKIP (previous gate skipped due to missing credentials)" -ForegroundColor DarkGray
            $allResults += New-OutcomeResult -Gate "Grab" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "Previous gate skipped due to missing credentials"
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
        elseif (-not $lastAlbumSearchResult -or -not $lastAlbumSearchResult.AlbumId) {
            Write-Host "       SKIP (no AlbumId from AlbumSearch gate - run with -Gate all)" -ForegroundColor Yellow
            $allResults += New-OutcomeResult -Gate "Grab" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "No AlbumId available (AlbumSearch gate not run or failed)"
            }
        }
        else {
            try {
                $credAllOf = @()
                $credAnyOf = @()
                if ($config.ContainsKey("CredentialAllOfFieldNames")) {
                    $credAllOf = @($config.CredentialAllOfFieldNames)
                }
                if ($config.ContainsKey("CredentialAnyOfFieldNames")) {
                    $credAnyOf = @($config.CredentialAnyOfFieldNames)
                }
                # Backward compatibility
                if ($config.ContainsKey("CredentialFieldNames")) {
                    $credAllOf = @($config.CredentialFieldNames)
                }

                $grabResult = Test-PluginGrabGate -IndexerId $lastPluginIndexer.id `
                    -PluginName $plugin `
                    -AlbumId $lastAlbumSearchResult.AlbumId `
                    -CredentialAllOfFieldNames $credAllOf `
                    -CredentialAnyOfFieldNames $credAnyOf `
                    -ContainerName $ContainerName `
                    -SkipIfNoCreds:$true

                $outcome = if ($grabResult.Outcome) { $grabResult.Outcome } elseif ($grabResult.Success) { "success" } else { "failed" }
                $allResults += New-OutcomeResult -Gate "Grab" -PluginName $plugin -Outcome $outcome -Errors $grabResult.Errors -Details @{
                    IndexerId = $lastPluginIndexer.id
                    AlbumId = $lastAlbumSearchResult.AlbumId
                    ReleaseTitle = $grabResult.ReleaseTitle
                    QueueItemId = $grabResult.QueueItemId
                    DownloadId = $grabResult.DownloadId
                    OutputPath = $grabResult.OutputPath
                    QueueStatus = $grabResult.QueueStatus
                    TrackedDownloadStatus = $grabResult.TrackedDownloadStatus
                    TrackedDownloadState = $grabResult.TrackedDownloadState
                    SampleFile = $grabResult.SampleFile
                    CredentialAllOf = $credAllOf
                    CredentialAnyOf = $credAnyOf
                    SkipReason = $grabResult.SkipReason
                }

                if ($outcome -eq "skipped") {
                    $reason = $grabResult.SkipReason
                    if (-not $reason) { $reason = "Skipped by gate policy" }
                    Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray
                }
                elseif ($grabResult.Success) {
                    Write-Host "       PASS (queued: $($grabResult.ReleaseTitle))" -ForegroundColor Green
                }
                else {
                    Write-Host "       FAIL" -ForegroundColor Red
                    foreach ($err in $grabResult.Errors) {
                        Write-Host "       - $err" -ForegroundColor Red
                    }
                    $overallSuccess = $false
                    $stopNow = $true
                    break
                }
            }
            catch {
                Write-Host "       ERROR: $_" -ForegroundColor Red
                $allResults += New-OutcomeResult -Gate "Grab" -PluginName $plugin -Outcome "failed" -Errors @("Grab gate error: $_")
                $overallSuccess = $false
                $stopNow = $true
                break
            }
        }
    }

    Write-Host ""
}

# Persistency gate is ecosystem-scoped (restarts container once), so run after the main loop.
if ($runPersist -and $overallSuccess) {
    Write-Host "Persist Gate..." -ForegroundColor Cyan
    $persistResult = Test-PersistGate -PluginList $pluginList -PluginConfigs $pluginConfigs -LidarrUrl $LidarrUrl -ApiKey $effectiveApiKey -ContainerName $ContainerName
    $allResults += New-OutcomeResult -Gate "Persist" -PluginName "Ecosystem" -Outcome $persistResult.Outcome -Errors $persistResult.Errors -Details $persistResult.Details

    if ($persistResult.Outcome -eq "success") {
        Write-Host "  PASS" -ForegroundColor Green
    }
    elseif ($persistResult.Outcome -eq "skipped") {
        Write-Host "  SKIP" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  FAIL" -ForegroundColor Red
        foreach ($err in $persistResult.Errors) {
            Write-Host "  - $err" -ForegroundColor Red
        }
        $overallSuccess = $false
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
