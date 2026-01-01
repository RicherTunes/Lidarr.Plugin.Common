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
    6. ImportList Gate (credentials required): Triggers ImportListSync, verifies sync completes (Brainarr)
    7. Persist Gate (optional): Restarts container and verifies configured components persist

    Combined:
    - bootstrap: configure + all + persist
    - all: schema + search + albumsearch + grab + importlist

    On failure, creates a diagnostics bundle for AI-assisted triage.

    RELATED SCRIPTS:
    - test-multi-plugin-persistent.ps1: Build + deploy + start Lidarr (run first)
    - test-qobuzarr-persistent.ps1: Single-plugin persistent testing

.PARAMETER Plugins
    Comma-separated list of plugins to test (e.g., "Qobuzarr,Tidalarr")

.PARAMETER Gate
    Which gate to run: "schema", "configure", "search", "albumsearch", "grab", "importlist", "all", "persist", or "bootstrap" (default: "schema")

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

.PARAMETER PersistRerun
    When set, re-runs Search gates after Persist gate to prove functional auth post-restart.
    Automatically enabled in bootstrap mode.

.PARAMETER ValidateMetadata
    When set, runs optional metadata tag validation after successful Grab gate.
    Checks that downloaded audio files have required tags (artist, album, title, track).
    Requires python3 + mutagen in the container. SKIPS (not fails) if mutagen unavailable.
    Can also be enabled via E2E_VALIDATE_METADATA=1 environment variable.

.PARAMETER MetadataFilesToCheck
    Maximum number of files to check for metadata validation (default: 3).

.PARAMETER ForceConfigUpdate
    When set, the Configure gate will update ALL fields from env vars (blast and converge mode).
    By default, only auth-related fields are updated to preserve user-configured settings like
    priority, tags, and quality preferences. Can also be enabled via E2E_FORCE_CONFIG_UPDATE=1.

.PARAMETER PostRestartGrab
    When set (with -Gate bootstrap), runs a full Grab gate after restart to prove tokens survive
    and downloads still work. More expensive than Revalidation (which only tests search).
    Can also be enabled via E2E_POST_RESTART_GRAB=1 environment variable.

.PARAMETER PostRestartGrabTimeoutMin
    Timeout in minutes to wait for post-restart grab to complete (default: 12).

.PARAMETER CleanupAfterPostRestartGrab
    When set, deletes downloaded files after PostRestartGrab validation passes.
    By default, files are kept for debugging.

.PARAMETER RunBrainarrLLMGate
    When set, runs the Brainarr LLM functional gate (proof-of-life + sync test).
    Requires BRAINARR_LLM_BASE_URL environment variable to be set.
    Can also be enabled via E2E_RUN_BRAINARR_LLM=1 environment variable.

.PARAMETER BrainarrLLMBaseUrl
    Base URL of the LLM endpoint (e.g., http://localhost:1234).
    Can also be set via BRAINARR_LLM_BASE_URL environment variable.

.PARAMETER BrainarrModelId
    Specific model ID to use (optional - auto-detects if not specified).
    Can also be set via BRAINARR_MODEL_ID environment variable.

.PARAMETER StrictBrainarr
    When set, FAIL instead of SKIP if LLM endpoint is unreachable.
    Can also be enabled via E2E_STRICT_BRAINARR=1 environment variable.

.PARAMETER BrainarrSyncTimeoutSec
    Timeout in seconds for Brainarr LLM sync command (default: 120).
    Increase for slow LLM endpoints. Can also be set via BRAINARR_SYNC_TIMEOUT_SEC.

.PARAMETER ConfigurePassIfAlreadyConfigured
    When set and env vars are missing, but plugin components already exist in Lidarr,
    the Configure gate returns SUCCESS (not SKIP). This enables idempotent runs where
    the gate passes once components have been configured (manually or in a prior run).
    Can also be enabled via E2E_CONFIGURE_PASS_IF_CONFIGURED=1 environment variable.

.NOTES
    Configure Gate Environment Variables:

    Qobuzarr (indexer + download client):
      - QOBUZARR_AUTH_TOKEN (required) - Qobuz session token
      - QOBUZARR_USER_ID (optional) - Qobuz user ID
      - QOBUZARR_COUNTRY_CODE (optional, default: US)

    Tidalarr (indexer + download client):
      - TIDALARR_CONFIG_PATH (required*) - OAuth state persistence path
      - TIDALARR_REDIRECT_URL (required*) - OAuth callback URL
      - TIDALARR_MARKET (optional, default: US)
      * At least one of CONFIG_PATH or REDIRECT_URL required

    Brainarr (import list only):
      - BRAINARR_LLM_BASE_URL (required) - LM Studio endpoint URL
      - BRAINARR_MODEL (optional) - Model name

    If required env vars are missing, Configure gate returns SKIP (not FAIL).

.EXAMPLE
    # Run all gates with metadata validation
    ./e2e-runner.ps1 -Plugins "Tidalarr" -Gate all -ValidateMetadata

.EXAMPLE
    # Run schema gate for all plugins (no credentials needed)
    ./e2e-runner.ps1 -Plugins "Qobuzarr,Tidalarr,Brainarr" -Gate schema

.EXAMPLE
    # Run all gates for Qobuzarr
    ./e2e-runner.ps1 -Plugins "Qobuzarr" -Gate all -ApiKey "your-api-key"

.EXAMPLE
    # Bootstrap with env var credentials (no UI needed)
    $env:QOBUZARR_AUTH_TOKEN = "your-token"
    $env:QOBUZARR_USER_ID = "12345"
    ./e2e-runner.ps1 -Plugins "Qobuzarr" -Gate bootstrap -LidarrUrl "http://localhost:8696"

.EXAMPLE
    # Force update all fields (overwrite user settings)
    $env:E2E_FORCE_CONFIG_UPDATE = "1"
    ./e2e-runner.ps1 -Plugins "Tidalarr" -Gate configure
#>
param(
    [Parameter(Mandatory)]
    [string]$Plugins,

    [ValidateSet("schema", "configure", "search", "albumsearch", "grab", "importlist", "all", "persist", "bootstrap")]
    [string]$Gate = "schema",

    [string]$LidarrUrl = "http://localhost:8686",

    [string]$ApiKey = $env:LIDARR_API_KEY,

    [switch]$ExtractApiKeyFromContainer,

    [string]$ContainerName = "lidarr-e2e-test",

    [string]$DiagnosticsPath = "./diagnostics",

    [switch]$SkipDiagnostics,

    [switch]$PersistRerun,

    [switch]$ValidateMetadata,

    [int]$MetadataFilesToCheck = 3,

    [switch]$ForceConfigUpdate,

    [switch]$PostRestartGrab,

    [int]$PostRestartGrabTimeoutMin = 12,

    [switch]$CleanupAfterPostRestartGrab,

    [switch]$RunBrainarrLLMGate,

    [string]$BrainarrLLMBaseUrl,

    [string]$BrainarrModelId,

    [switch]$StrictBrainarr,

    [int]$BrainarrSyncTimeoutSec = 120,

    # JSON output path for machine-readable manifest (default: diagnostics/run-manifest.json)
    [string]$JsonOutputPath,

    # Emit JSON manifest (auto-enabled if JsonOutputPath specified)
    [switch]$EmitJson,

    # When env vars are missing but components already exist, return success (not skip)
    # This enables idempotent runs where Configure gate passes once components are configured
    [switch]$ConfigurePassIfAlreadyConfigured,

    # Preferred component IDs state file (used to make persistent runs deterministic)
    # If not specified, defaults to .e2e-bootstrap/e2e-component-ids.json       
    [string]$ComponentIdsPath,

    # Optional salt to avoid preferred-ID collisions when reusing the same
    # (LidarrUrl + ContainerName) against different config dirs/instances.
    # Can also be set via E2E_COMPONENT_IDS_INSTANCE_SALT.
    [string]$ComponentIdsInstanceSalt,

    # Disable reading preferred IDs (always use discovery). Writing may still occur if enabled.
    [switch]$DisableStoredComponentIds,

    # Disable writing/updating the component IDs state file.
    [switch]$DisableComponentIdPersistence
)

# Environment variable override for ForceConfigUpdate
if (-not $ForceConfigUpdate -and $env:E2E_FORCE_CONFIG_UPDATE -eq '1') {
    $ForceConfigUpdate = $true
}

# Environment variable override for PostRestartGrab
if (-not $PostRestartGrab -and $env:E2E_POST_RESTART_GRAB -eq '1') {
    $PostRestartGrab = $true
}

# Environment variable overrides for BrainarrLLM gate
if (-not $RunBrainarrLLMGate -and $env:E2E_RUN_BRAINARR_LLM -eq '1') {
    $RunBrainarrLLMGate = $true
}
if (-not $BrainarrLLMBaseUrl -and $env:BRAINARR_LLM_BASE_URL) {
    $BrainarrLLMBaseUrl = $env:BRAINARR_LLM_BASE_URL
}
if (-not $BrainarrModelId) {
    # Support both BRAINARR_MODEL_ID and BRAINARR_MODEL (fallback)
    if ($env:BRAINARR_MODEL_ID) {
        $BrainarrModelId = $env:BRAINARR_MODEL_ID
    } elseif ($env:BRAINARR_MODEL) {
        $BrainarrModelId = $env:BRAINARR_MODEL
    }
}
if (-not $StrictBrainarr -and $env:E2E_STRICT_BRAINARR -eq '1') {
    $StrictBrainarr = $true
}
if ($env:BRAINARR_SYNC_TIMEOUT_SEC) {
    $parsed = 0
    if ([int]::TryParse($env:BRAINARR_SYNC_TIMEOUT_SEC, [ref]$parsed) -and $parsed -gt 0) {
        $BrainarrSyncTimeoutSec = $parsed
    }
}

# Environment variable override for ConfigurePassIfAlreadyConfigured
if (-not $ConfigurePassIfAlreadyConfigured -and $env:E2E_CONFIGURE_PASS_IF_CONFIGURED -eq '1') {
    $ConfigurePassIfAlreadyConfigured = $true
}

# Default JSON output path if EmitJson requested
if ($EmitJson -and -not $JsonOutputPath) {
    $JsonOutputPath = Join-Path $DiagnosticsPath "run-manifest.json"
}
# If JsonOutputPath specified, enable EmitJson
if ($JsonOutputPath) {
    $EmitJson = $true
}

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $PSScriptRoot

# Import modules
Import-Module (Join-Path $PSScriptRoot "lib/e2e-gates.psm1") -Force       
Import-Module (Join-Path $PSScriptRoot "lib/e2e-diagnostics.psm1") -Force 
Import-Module (Join-Path $PSScriptRoot "lib/e2e-json-output.psm1") -Force 
Import-Module (Join-Path $PSScriptRoot "lib/e2e-component-ids.psm1") -Force

# Preferred component IDs state (best-effort; never blocks runs)
if (-not $ComponentIdsPath) {
    $ComponentIdsPath = $env:E2E_COMPONENT_IDS_PATH
}
if (-not $ComponentIdsPath) {
    $ComponentIdsPath = Get-E2EComponentIdsDefaultPath -RepoRoot $scriptRoot
}
$script:E2EComponentIdsPath = $ComponentIdsPath
if (-not $ComponentIdsInstanceSalt) {
    $ComponentIdsInstanceSalt = $env:E2E_COMPONENT_IDS_INSTANCE_SALT
}
$script:E2EComponentIdsInstanceKey = Get-E2EComponentIdsInstanceKey -LidarrUrl $LidarrUrl -ContainerName $ContainerName -InstanceSalt ($ComponentIdsInstanceSalt ?? "")
$script:E2EComponentIdsState = Read-E2EComponentIdsState -Path $script:E2EComponentIdsPath
$script:E2EComponentResolution = @{}
$script:E2EComponentResolutionCandidates = @{}

function Save-E2EComponentIdsIfEnabled {
    param(
        [Parameter(Mandatory)]
        [string]$PluginName,

        [AllowNull()]
        [hashtable]$ComponentIds
    )

    if ($DisableComponentIdPersistence) { return }
    if ($null -eq $ComponentIds) { return }

    $safeResolutions = @("preferredId", "implementationName", "implementation", "created")
    $resolvedIndexer = Get-HashtableStringOrDefault -Table $script:E2EComponentResolution -Key "indexer|$PluginName" -DefaultValue "none"
    $resolvedClient = Get-HashtableStringOrDefault -Table $script:E2EComponentResolution -Key "downloadclient|$PluginName" -DefaultValue "none"
    $resolvedImportList = Get-HashtableStringOrDefault -Table $script:E2EComponentResolution -Key "importlist|$PluginName" -DefaultValue "none"

    if ($ComponentIds.ContainsKey("indexerId") -and ($ComponentIds["indexerId"] -as [int]) -gt 0 -and ($safeResolutions -contains $resolvedIndexer)) {
        Set-E2EPreferredComponentId -State $script:E2EComponentIdsState -InstanceKey $script:E2EComponentIdsInstanceKey -LidarrUrl $LidarrUrl -ContainerName $ContainerName -PluginName $PluginName -Type "indexer" -Id ([int]$ComponentIds["indexerId"])
    }
    if ($ComponentIds.ContainsKey("downloadClientId") -and ($ComponentIds["downloadClientId"] -as [int]) -gt 0 -and ($safeResolutions -contains $resolvedClient)) {
        Set-E2EPreferredComponentId -State $script:E2EComponentIdsState -InstanceKey $script:E2EComponentIdsInstanceKey -LidarrUrl $LidarrUrl -ContainerName $ContainerName -PluginName $PluginName -Type "downloadclient" -Id ([int]$ComponentIds["downloadClientId"])
    }
    if ($ComponentIds.ContainsKey("importListId") -and ($ComponentIds["importListId"] -as [int]) -gt 0 -and ($safeResolutions -contains $resolvedImportList)) {
        Set-E2EPreferredComponentId -State $script:E2EComponentIdsState -InstanceKey $script:E2EComponentIdsInstanceKey -LidarrUrl $LidarrUrl -ContainerName $ContainerName -PluginName $PluginName -Type "importlist" -Id ([int]$ComponentIds["importListId"])
    }

    try {
        $ok = Write-E2EComponentIdsState -Path $script:E2EComponentIdsPath -State $script:E2EComponentIdsState
        if (-not $ok) {
            Write-Warning "Preferred component IDs could not be persisted to '$($script:E2EComponentIdsPath)'. Continuing (best-effort)."
        }
    }
    catch {
        Write-Warning "Preferred component IDs could not be persisted to '$($script:E2EComponentIdsPath)'. Continuing (best-effort). Error: $($_.Exception.Message)"
    }
}

function Get-HashtableStringOrDefault {
    param(
        [AllowNull()]
        [hashtable]$Table,

        [Parameter(Mandatory)]
        [string]$Key,

        [Parameter(Mandatory)]
        [string]$DefaultValue
    )

    if ($null -eq $Table) { return $DefaultValue }
    if (-not $Table.ContainsKey($Key)) { return $DefaultValue }

    $value = $Table[$Key]
    if ($null -eq $value) { return $DefaultValue }

    $text = "$value"
    if ([string]::IsNullOrWhiteSpace($text)) { return $DefaultValue }
    return $text
}

function Get-ComponentAmbiguityDetails {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("indexer", "downloadclient", "importlist")]
        [string]$Type,

        [Parameter(Mandatory)]
        [string]$PluginName
    )

    $key = "$Type|$PluginName"
    $resolution = Get-HashtableStringOrDefault -Table $script:E2EComponentResolution -Key $key -DefaultValue "none"
    $candidateIds = @()
    if ($script:E2EComponentResolutionCandidates -and $script:E2EComponentResolutionCandidates.ContainsKey($key)) {
        $candidateIds = @($script:E2EComponentResolutionCandidates[$key])
    }

    if ($resolution -like "ambiguous*") {
        return @{
            IsAmbiguous = $true
            Resolution = $resolution
            CandidateIds = $candidateIds
        }
    }

    return @{
        IsAmbiguous = $false
        Resolution = $resolution
        CandidateIds = $candidateIds
    }
}

# Environment variable overrides for opt-in features
if (-not $ValidateMetadata -and $env:E2E_VALIDATE_METADATA -eq '1') {
    $ValidateMetadata = $true
}   

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

        [hashtable]$Details = @{},

        [DateTime]$StartTime = [DateTime]::MinValue,

        [DateTime]$EndTime = [DateTime]::MinValue
    )

    return [PSCustomObject]@{
        Gate = $Gate
        PluginName = $PluginName
        Outcome = $Outcome
        Success = ($Outcome -eq "success")
        Errors = $Errors
        Details = $Details
        StartTime = if ($StartTime -ne [DateTime]::MinValue) { $StartTime } else { $null }
        EndTime = if ($EndTime -ne [DateTime]::MinValue) { $EndTime } else { $null }
    }
}

<#
.SYNOPSIS
    Normalizes nested credential arrays to plain string arrays for JSON serialization.
    Converts PowerShell Object[] arrays to simple string[] to avoid CLR metadata bloat.
    Returns a List of string arrays for proper JSON serialization.
#>
function ConvertTo-PlainStringArray {
    param($InputArray)
    if ($null -eq $InputArray) { return @() }
    if (-not ($InputArray -is [System.Collections.IEnumerable]) -or $InputArray -is [string]) {
        return @([string]$InputArray)
    }
    if (@($InputArray).Count -eq 0) { return @() }

    # Convert to a proper array first
    $arr = @($InputArray)

    # Check if it's an array of arrays (credential groups)
    # Use List<string[]> for clean JSON serialization
    $result = [System.Collections.Generic.List[string[]]]::new()
    foreach ($item in $arr) {
        if ($null -eq $item) { continue }
        if ($item -is [System.Collections.IEnumerable] -and $item -isnot [string]) {
            # It's a nested array/group - convert each element to string
            $group = [System.Collections.Generic.List[string]]::new()
            foreach ($subItem in $item) {
                $group.Add([string]$subItem)
            }
            $result.Add([string[]]$group.ToArray())
        } else {
            # Single value - wrap in array
            $result.Add([string[]]@([string]$item))
        }
    }
    return $result
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

    $preferredId = $null
    if (-not $DisableStoredComponentIds) {
        $preferredId = Get-E2EPreferredComponentId -State $script:E2EComponentIdsState -InstanceKey $script:E2EComponentIdsInstanceKey -PluginName $PluginName -Type $Type
    }

    $preferredIdValue = if ($null -ne $preferredId) { [int]$preferredId } else { 0 }
    $selection = Select-ConfiguredComponent -Items $items -PluginName $PluginName -PreferredId $preferredIdValue
    $script:E2EComponentResolution["$Type|$PluginName"] = $selection.Resolution
    if ($selection.PSObject.Properties["CandidateIds"] -and $selection.CandidateIds) {
        $script:E2EComponentResolutionCandidates["$Type|$PluginName"] = @($selection.CandidateIds)
    }
    else {
        $script:E2EComponentResolutionCandidates.Remove("$Type|$PluginName") | Out-Null
    }
    return $selection.Component
}

# =============================================================================
# Configure Gate: Env-Var-Driven Component Management
# =============================================================================

<#
.SYNOPSIS
    Returns plugin-specific env var configuration for component creation.
.DESCRIPTION
    Reads environment variables for a given plugin and returns a hashtable
    with auth fields and their values. Returns $null for missing required vars.
.OUTPUTS
    Hashtable with keys: AuthFields (for indexer/client), ImportListFields (for Brainarr),
    MissingRequired (array of missing required env var names), PluginName.
#>
function Get-PluginEnvConfig {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Qobuzarr", "Tidalarr", "Brainarr")]
        [string]$PluginName
    )

    $config = @{
        PluginName = $PluginName
        IndexerFields = @{}
        DownloadClientFields = @{}
        ImportListFields = @{}
        MissingRequired = @()
        HasRequiredEnvVars = $false
    }

    switch ($PluginName) {
        "Qobuzarr" {
            # Required: QOBUZARR_AUTH_TOKEN
            # Optional: QOBUZARR_USER_ID, QOBUZARR_COUNTRY_CODE
            $authToken = $env:QOBUZARR_AUTH_TOKEN
            if ([string]::IsNullOrWhiteSpace($authToken)) {
                $authToken = $env:QOBUZ_AUTH_TOKEN
            }

            if ([string]::IsNullOrWhiteSpace($authToken)) {
                $config.MissingRequired += "QOBUZARR_AUTH_TOKEN"
            } else {
                $config.HasRequiredEnvVars = $true
                $config.IndexerFields["authMethod"] = 1  # Token auth
                $config.IndexerFields["authToken"] = $authToken

                # Optional fields
                $userId = $env:QOBUZARR_USER_ID
                if ([string]::IsNullOrWhiteSpace($userId)) { $userId = $env:QOBUZ_USER_ID }
                if (-not [string]::IsNullOrWhiteSpace($userId)) {
                    $config.IndexerFields["userId"] = $userId
                }

                $countryCode = $env:QOBUZARR_COUNTRY_CODE
                if ([string]::IsNullOrWhiteSpace($countryCode)) { $countryCode = $env:QOBUZ_COUNTRY_CODE }
                if ([string]::IsNullOrWhiteSpace($countryCode)) { $countryCode = "US" }
                $config.IndexerFields["countryCode"] = $countryCode

                # Download client uses same auth (shared session)
                # Download client has no auth fields - just storage/quality settings
                $downloadPath = $env:QOBUZARR_DOWNLOAD_PATH
                if ([string]::IsNullOrWhiteSpace($downloadPath)) { $downloadPath = "/downloads/qobuzarr" }
                $config.DownloadClientFields["downloadPath"] = $downloadPath
                $config.DownloadClientFields["createAlbumFolders"] = $true
                $config.DownloadClientFields["preferredQuality"] = 6  # FLAC CD
            }
        }

        "Tidalarr" {
            # Tidalarr uses OAuth - needs configPath for token persistence
            # Mode 1: Tokens already exist in configPath (just set configPath)
            # Mode 2: Fresh OAuth setup (set redirectUrl + configPath for token storage)
            #
            # Env vars:
            #   TIDALARR_CONFIG_PATH - Where tokens are stored (default: /config/plugins/RicherTunes/Tidalarr)
            #   TIDALARR_REDIRECT_URL - OAuth callback URL (only needed for initial setup)
            #   TIDALARR_MARKET - Market/region (default: US)
            #   TIDALARR_DOWNLOAD_PATH - Download location (default: /downloads/tidalarr)

            $configPath = $env:TIDALARR_CONFIG_PATH
            $redirectUrl = $env:TIDALARR_REDIRECT_URL

            # Require at least one of configPath or redirectUrl to proceed
            if ([string]::IsNullOrWhiteSpace($configPath) -and [string]::IsNullOrWhiteSpace($redirectUrl)) {
                $config.MissingRequired += "TIDALARR_CONFIG_PATH or TIDALARR_REDIRECT_URL"
            } else {
                $config.HasRequiredEnvVars = $true

                # If redirectUrl is set but configPath isn't, use safe default for token storage
                if ([string]::IsNullOrWhiteSpace($configPath)) {
                    $configPath = "/config/plugins/RicherTunes/Tidalarr"
                }
                $config.IndexerFields["configPath"] = $configPath
                $config.DownloadClientFields["configPath"] = $configPath

                # Only set redirectUrl if explicitly provided (don't overwrite with empty)
                if (-not [string]::IsNullOrWhiteSpace($redirectUrl)) {
                    $config.IndexerFields["redirectUrl"] = $redirectUrl
                    $config.DownloadClientFields["redirectUrl"] = $redirectUrl
                }

                $market = $env:TIDALARR_MARKET
                if ([string]::IsNullOrWhiteSpace($market)) { $market = "US" }
                $config.IndexerFields["tidalMarket"] = $market
                $config.DownloadClientFields["tidalMarket"] = $market

                # Download path for download client
                $downloadPath = $env:TIDALARR_DOWNLOAD_PATH
                if ([string]::IsNullOrWhiteSpace($downloadPath)) { $downloadPath = "/downloads/tidalarr" }
                $config.DownloadClientFields["downloadPath"] = $downloadPath
            }
        }

        "Brainarr" {
            # Brainarr import list uses AI for music discovery
            # Env vars:
            #   BRAINARR_LLM_BASE_URL  - Local provider URL (Ollama/LM Studio) - REQUIRED
            #   BRAINARR_MODEL         - Override model ID (optional)
            #   BRAINARR_PROVIDER      - Provider enum value (optional, let Lidarr default if unset)

            $llmBaseUrl = $env:BRAINARR_LLM_BASE_URL

            if ([string]::IsNullOrWhiteSpace($llmBaseUrl)) {
                $config.MissingRequired += "BRAINARR_LLM_BASE_URL"
            } else {
                $config.HasRequiredEnvVars = $true

                # configurationUrl is the schema field for LLM endpoint
                $config.ImportListFields["configurationUrl"] = $llmBaseUrl

                # Provider - only set if explicitly specified (let Lidarr default otherwise)
                $provider = $env:BRAINARR_PROVIDER
                if (-not [string]::IsNullOrWhiteSpace($provider)) {
                    $config.ImportListFields["provider"] = [int]$provider
                }

                # Model selection logic:
                # - If BRAINARR_MODEL set: use manualModelId + autoDetectModel=false
                # - If not set: autoDetectModel=true (let Brainarr pick best model)
                $model = $env:BRAINARR_MODEL
                if (-not [string]::IsNullOrWhiteSpace($model)) {
                    $config.ImportListFields["manualModelId"] = $model
                    $config.ImportListFields["autoDetectModel"] = $false
                } else {
                    $config.ImportListFields["autoDetectModel"] = $true
                }
            }
        }
    }

    return $config
}

<#
.SYNOPSIS
    Fetches the schema for a specific component type and implementation.
.OUTPUTS
    The schema object from /api/v1/{type}/schema matching the implementation.
#>
function Get-ComponentSchema {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("indexer", "downloadclient", "importlist")]
        [string]$Type,

        [Parameter(Mandatory)]
        [string]$ImplementationMatch
    )

    $schemas = Invoke-LidarrApi -Endpoint "$Type/schema"
    if (-not $schemas) { return $null }

    return $schemas | Where-Object {
        $_.implementationName -like "*$ImplementationMatch*" -or
        $_.implementation -like "*$ImplementationMatch*"
    } | Select-Object -First 1
}

<#
.SYNOPSIS
    Creates a new component from schema and env var config.
.DESCRIPTION
    Uses the schema as a template, populates fields from envConfig,
    and POSTs to create the component. Returns the created component or $null.
#>
function New-ComponentFromEnv {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("indexer", "downloadclient", "importlist")]
        [string]$Type,

        [Parameter(Mandatory)]
        [string]$PluginName,

        [Parameter(Mandatory)]
        $Schema,

        [Parameter(Mandatory)]
        [hashtable]$FieldValues
    )

    # Clone schema to avoid mutation
    $payload = $Schema | ConvertTo-Json -Depth 10 | ConvertFrom-Json

    # Set name and enable
    $payload.name = $PluginName
    if ($null -ne $payload.enable) { $payload.enable = $true }
    if ($null -ne $payload.enableRss) { $payload.enableRss = $false }
    if ($null -ne $payload.enableAutomaticSearch) { $payload.enableAutomaticSearch = $true }
    if ($null -ne $payload.enableInteractiveSearch) { $payload.enableInteractiveSearch = $true }

    # Populate fields from env config
    foreach ($fieldName in $FieldValues.Keys) {
        $value = $FieldValues[$fieldName]
        $payload.fields = Set-LidarrFieldValue -Fields $payload.fields -Name $fieldName -Value $value
    }

    # Remove id if present (for creation)
    if ($payload.PSObject.Properties['id']) {
        $payload.PSObject.Properties.Remove('id')
    }

    try {
        $created = Invoke-LidarrApi -Endpoint $Type -Method POST -Body $payload
        return $created
    }
    catch {
        Write-Warning "Failed to create $Type for $PluginName : $_"
        return $null
    }
}

<#
.SYNOPSIS
    Updates only auth-related fields on an existing component.
.DESCRIPTION
    Fetches the full component, updates only the specified auth fields,
    and PUTs the update. Does NOT touch user-configured fields like priority, tags, etc.
.PARAMETER ForceUpdate
    When true, updates all fields from envConfig (blast and converge mode).
    When false (default), only updates auth fields if they differ.
#>
function Update-ComponentAuthFields {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("indexer", "downloadclient", "importlist")]
        [string]$Type,

        [Parameter(Mandatory)]
        $ExistingComponent,

        [Parameter(Mandatory)]
        [hashtable]$FieldValues,

        [switch]$ForceUpdate
    )

    # Define which fields are "auth fields" per plugin - these are safe to update
    $authFieldNames = @(
        # Qobuzarr auth
        "authMethod", "authToken", "userId", "countryCode",
        # Tidalarr auth
        "configPath", "redirectUrl", "tidalMarket",
        # Brainarr
        "configurationUrl", "manualModelId", "autoDetectModel", "provider",
        # Common safe fields (not user-tuned)
        "downloadPath"
    )

    $fullComponent = Invoke-LidarrApi -Endpoint "$Type/$($ExistingComponent.id)"
    $needsUpdate = $false
    $updatedFields = @($fullComponent.fields)
    $changedFieldNames = @()

    foreach ($fieldName in $FieldValues.Keys) {
        # Skip non-auth fields unless ForceUpdate
        if (-not $ForceUpdate -and $fieldName -notin $authFieldNames) {
            continue
        }

        $currentValue = Get-LidarrFieldValue -Fields $fullComponent.fields -Name $fieldName
        $newValue = $FieldValues[$fieldName]

        # Sensitive fields that Lidarr masks with ******** (comprehensive list)
        # Also includes PII fields (userId, email) that should not appear in logs
        $sensitiveFields = @(
            # Auth secrets (Lidarr masks these)
            "authToken", "password", "redirectUrl", "appSecret",
            "refreshToken", "accessToken", "clientSecret",
            "apiKey", "secret", "token",
            # PII (not masked by Lidarr but should be redacted in logs)
            "userId", "email", "username",
            # Internal URLs (may reveal infrastructure)
            "configurationUrl"
        )

        # Only update if value differs (idempotency)
        $isDifferent = $false
        $skippedMasked = $false

        # Handle masked sensitive fields: if current value is masked (********) and we have a value,
        # assume it's already configured correctly unless ForceUpdate is set
        if ($fieldName -in $sensitiveFields -and "$currentValue" -eq "********" -and -not [string]::IsNullOrWhiteSpace("$newValue")) {
            if ($ForceUpdate) {
                $isDifferent = $true  # Force update even if masked
            } else {
                $skippedMasked = $true  # Track for logging
            }
        }
        elseif ($null -eq $currentValue -and $null -ne $newValue) {
            $isDifferent = $true
        } elseif ($null -ne $currentValue -and $null -eq $newValue) {
            $isDifferent = $true
        } elseif ("$currentValue" -ne "$newValue") {
            $isDifferent = $true
        }

        if ($isDifferent) {
            $updatedFields = Set-LidarrFieldValue -Fields $updatedFields -Name $fieldName -Value $newValue
            $needsUpdate = $true
            # Never log sensitive values - use the same sensitiveFields list for consistency
            if ($fieldName -in $sensitiveFields) {
                $changedFieldNames += "$fieldName=[REDACTED]"
            } else {
                $changedFieldNames += "$fieldName=$newValue"
            }
        } elseif ($skippedMasked) {
            # Log that we skipped a masked field (drift warning)
            $changedFieldNames += "$fieldName=[MASKED; skipping compare - use -ForceConfigUpdate to overwrite]"
        }
    }

    if (-not $needsUpdate) {
        # Include any masked field notes in the result even if no update needed
        return @{ Updated = $false; Component = $fullComponent; ChangedFields = $changedFieldNames }
    }

    $fullComponent.fields = $updatedFields

    try {
        $updated = Invoke-LidarrApi -Endpoint "$Type/$($fullComponent.id)" -Method PUT -Body $fullComponent
        return @{ Updated = $true; Component = $updated; ChangedFields = $changedFieldNames }
    }
    catch {
        # Fallback for some Lidarr versions
        $updated = Invoke-LidarrApi -Endpoint $Type -Method PUT -Body $fullComponent
        return @{ Updated = $true; Component = $updated; ChangedFields = $changedFieldNames }
    }
}

function Test-ConfigureGateForPlugin {
    param(
        [Parameter(Mandatory)]
        [string]$PluginName,

        [hashtable]$PluginConfig = @{},

        [switch]$ForceUpdate,

        [switch]$PassIfAlreadyConfigured
    )

    $result = [PSCustomObject]@{
        Gate = "Configure"
        PluginName = $PluginName
        Outcome = "skipped" # success|failed|skipped
        Success = $false
        Actions = @()
        Errors = @()
        SkipReason = $null
        Details = @{
            componentIds = @{}
            alreadyConfigured = $false
            created = $false
            updated = $false
            forceUpdateUsed = $ForceUpdate.IsPresent
            resolution = @{}
            preferredIdUsed = $false
        }
    }

    # ==========================================================================
    # Step 1: Check if plugin is supported for env-var configuration
    # ==========================================================================
    $supportedPlugins = @("Qobuzarr", "Tidalarr", "Brainarr")
    if ($PluginName -notin $supportedPlugins) {
        $result.Outcome = "success"
        $result.Success = $true
        $result.Actions += "Plugin '$PluginName' not in supported list; no configuration needed"
        return $result
    }

    try {
        # ==========================================================================
        # Step 2: Get env var configuration for this plugin
        # ==========================================================================
        $envConfig = Get-PluginEnvConfig -PluginName $PluginName

        if (-not $envConfig.HasRequiredEnvVars) {
            # Missing required env vars - check if components already exist
            $missingList = $envConfig.MissingRequired -join ", "

            if ($PassIfAlreadyConfigured) {
                # Check if required components already exist
                $existingComponents = @{}
                $allRequiredExist = $true

                if ($PluginName -in @("Qobuzarr", "Tidalarr")) {
                    $indexer = Find-ConfiguredComponent -Type "indexer" -PluginName $PluginName
                    $client = Find-ConfiguredComponent -Type "downloadclient" -PluginName $PluginName

                    if (-not $indexer) {
                        $amb = Get-ComponentAmbiguityDetails -Type "indexer" -PluginName $PluginName
                        if ($amb.IsAmbiguous) {
                            $result.Outcome = "failed"
                            $result.Success = $false
                            $result.Errors += "Ambiguous configured indexer selection for $PluginName ($($amb.Resolution)): IDs=$($amb.CandidateIds -join ',')"
                            $result.Details.resolution["indexer"] = $amb.Resolution
                            $result.Details.componentIds["indexerCandidateIds"] = $amb.CandidateIds
                            return $result
                        }
                    }
                    if (-not $client) {
                        $amb = Get-ComponentAmbiguityDetails -Type "downloadclient" -PluginName $PluginName
                        if ($amb.IsAmbiguous) {
                            $result.Outcome = "failed"
                            $result.Success = $false
                            $result.Errors += "Ambiguous configured download client selection for $PluginName ($($amb.Resolution)): IDs=$($amb.CandidateIds -join ',')"
                            $result.Details.resolution["downloadclient"] = $amb.Resolution
                            $result.Details.componentIds["downloadClientCandidateIds"] = $amb.CandidateIds
                            return $result
                        }
                    }

                    if ($indexer) { $existingComponents["indexerId"] = $indexer.id }
                    if ($client) { $existingComponents["downloadClientId"] = $client.id }

                    if (-not $indexer -or -not $client) {
                        $allRequiredExist = $false
                    }
                }
                elseif ($PluginName -eq "Brainarr") {
                    $importList = Find-ConfiguredComponent -Type "importlist" -PluginName $PluginName

                    if (-not $importList) {
                        $amb = Get-ComponentAmbiguityDetails -Type "importlist" -PluginName $PluginName
                        if ($amb.IsAmbiguous) {
                            $result.Outcome = "failed"
                            $result.Success = $false
                            $result.Errors += "Ambiguous configured import list selection for $PluginName ($($amb.Resolution)): IDs=$($amb.CandidateIds -join ',')"
                            $result.Details.resolution["importlist"] = $amb.Resolution
                            $result.Details.componentIds["importListCandidateIds"] = $amb.CandidateIds
                            return $result
                        }
                    }

                    if ($importList) { $existingComponents["importListId"] = $importList.id }
                    else { $allRequiredExist = $false }
                }

                if ($allRequiredExist -and $existingComponents.Count -gt 0) {
                    # Components exist - return success (idempotent)
                    $result.Outcome = "success"
                    $result.Success = $true
                    $result.Details.componentIds = $existingComponents
                    $result.Details.alreadyConfigured = $true
                    if ($PluginName -in @("Qobuzarr", "Tidalarr")) {
                        $result.Details.resolution["indexer"] = Get-HashtableStringOrDefault -Table $script:E2EComponentResolution -Key "indexer|$PluginName" -DefaultValue "unknown"
                        $result.Details.resolution["downloadclient"] = Get-HashtableStringOrDefault -Table $script:E2EComponentResolution -Key "downloadclient|$PluginName" -DefaultValue "unknown"
                    }
                    elseif ($PluginName -eq "Brainarr") {
                        $result.Details.resolution["importlist"] = Get-HashtableStringOrDefault -Table $script:E2EComponentResolution -Key "importlist|$PluginName" -DefaultValue "unknown"
                    }
                    $result.Details.preferredIdUsed = ($result.Details.resolution.Values -contains "preferredId")
                    Save-E2EComponentIdsIfEnabled -PluginName $PluginName -ComponentIds $existingComponents
                    $result.Actions += "Env vars missing; existing component(s) present -> no-op"
                    return $result
                }
            }

            # Components missing or PassIfAlreadyConfigured not set -> SKIP
            $result.SkipReason = "Missing env vars: $missingList"
            return $result
        }

        # ==========================================================================
        # Step 3: Configure Indexer (Qobuzarr, Tidalarr)
        # ==========================================================================
        if ($PluginName -in @("Qobuzarr", "Tidalarr")) {
            $indexer = Find-ConfiguredComponent -Type "indexer" -PluginName $PluginName

            if (-not $indexer) {
                # Create new indexer from schema
                $schema = Get-ComponentSchema -Type "indexer" -ImplementationMatch $PluginName
                if (-not $schema) {
                    $result.Errors += "Indexer schema not found for $PluginName"
                    $result.Outcome = "failed"
                    return $result
                }

                $created = New-ComponentFromEnv -Type "indexer" -PluginName $PluginName -Schema $schema -FieldValues $envConfig.IndexerFields
                if ($created) {
                    $result.Actions += "Created indexer '$PluginName' (id=$($created.id))"
                    $result.Details.componentIds["indexerId"] = $created.id     
                    $result.Details.created = $true
                    $result.Details.resolution["indexer"] = "created"
                } else {
                    $result.Errors += "Failed to create indexer for $PluginName"
                    $result.Outcome = "failed"
                    return $result
                }
            } else {
                # Update existing indexer (auth fields only, unless ForceUpdate)
                $result.Details.componentIds["indexerId"] = $indexer.id   
                $result.Details.resolution["indexer"] = Get-HashtableStringOrDefault -Table $script:E2EComponentResolution -Key "indexer|$PluginName" -DefaultValue "implementationName"
                $updateResult = Update-ComponentAuthFields -Type "indexer" -ExistingComponent $indexer -FieldValues $envConfig.IndexerFields -ForceUpdate:$ForceUpdate
                if ($updateResult.Updated) {
                    $result.Actions += "Updated indexer auth fields: $($updateResult.ChangedFields -join ', ')"
                    $result.Details.updated = $true
                } elseif ($updateResult.ChangedFields -and $updateResult.ChangedFields.Count -gt 0) {
                    $result.Actions += "Indexer '$PluginName' already configured: $($updateResult.ChangedFields -join ', ')"
                } else {
                    $result.Actions += "Indexer '$PluginName' already configured (no changes)"
                }
            }
        }

        # ==========================================================================
        # Step 4: Configure Download Client (Qobuzarr, Tidalarr)
        # ==========================================================================
        if ($PluginName -in @("Qobuzarr", "Tidalarr")) {
            $client = Find-ConfiguredComponent -Type "downloadclient" -PluginName $PluginName

            if (-not $client) {
                # Create new download client from schema
                $schema = Get-ComponentSchema -Type "downloadclient" -ImplementationMatch $PluginName
                if (-not $schema) {
                    $result.Errors += "Download client schema not found for $PluginName"
                    $result.Outcome = "failed"
                    return $result
                }

                $created = New-ComponentFromEnv -Type "downloadclient" -PluginName $PluginName -Schema $schema -FieldValues $envConfig.DownloadClientFields
                if ($created) {
                    $result.Actions += "Created download client '$PluginName' (id=$($created.id))"
                    $result.Details.componentIds["downloadClientId"] = $created.id
                    $result.Details.created = $true
                    $result.Details.resolution["downloadclient"] = "created"
                } else {
                    $result.Errors += "Failed to create download client for $PluginName"
                    $result.Outcome = "failed"
                    return $result
                }
            } else {
                # Update existing download client (auth fields only, unless ForceUpdate)
                $result.Details.componentIds["downloadClientId"] = $client.id
                $result.Details.resolution["downloadclient"] = Get-HashtableStringOrDefault -Table $script:E2EComponentResolution -Key "downloadclient|$PluginName" -DefaultValue "implementationName"
                $updateResult = Update-ComponentAuthFields -Type "downloadclient" -ExistingComponent $client -FieldValues $envConfig.DownloadClientFields -ForceUpdate:$ForceUpdate
                if ($updateResult.Updated) {
                    $result.Actions += "Updated download client auth fields: $($updateResult.ChangedFields -join ', ')"
                    $result.Details.updated = $true
                } elseif ($updateResult.ChangedFields -and $updateResult.ChangedFields.Count -gt 0) {
                    $result.Actions += "Download client '$PluginName' already configured: $($updateResult.ChangedFields -join ', ')"
                } else {
                    $result.Actions += "Download client '$PluginName' already configured (no changes)"
                }
            }
        }

        # ==========================================================================
        # Step 5: Configure Import List (Brainarr only)
        # ==========================================================================
        if ($PluginName -eq "Brainarr") {
            $importList = Find-ConfiguredComponent -Type "importlist" -PluginName $PluginName

            if (-not $importList) {
                # Create new import list from schema
                $schema = Get-ComponentSchema -Type "importlist" -ImplementationMatch $PluginName
                if (-not $schema) {
                    $result.Errors += "Import list schema not found for $PluginName"
                    $result.Outcome = "failed"
                    return $result
                }

                $created = New-ComponentFromEnv -Type "importlist" -PluginName $PluginName -Schema $schema -FieldValues $envConfig.ImportListFields
                if ($created) {
                    $result.Actions += "Created import list '$PluginName' (id=$($created.id))"
                    $result.Details.componentIds["importListId"] = $created.id
                    $result.Details.created = $true
                    $result.Details.resolution["importlist"] = "created"
                } else {
                    $result.Errors += "Failed to create import list for $PluginName"
                    $result.Outcome = "failed"
                    return $result
                }
            } else {
                # Update existing import list
                $result.Details.componentIds["importListId"] = $importList.id
                $result.Details.resolution["importlist"] = Get-HashtableStringOrDefault -Table $script:E2EComponentResolution -Key "importlist|$PluginName" -DefaultValue "implementationName"
                $updateResult = Update-ComponentAuthFields -Type "importlist" -ExistingComponent $importList -FieldValues $envConfig.ImportListFields -ForceUpdate:$ForceUpdate
                if ($updateResult.Updated) {
                    $result.Actions += "Updated import list fields: $($updateResult.ChangedFields -join ', ')"
                    $result.Details.updated = $true
                } elseif ($updateResult.ChangedFields -and $updateResult.ChangedFields.Count -gt 0) {
                    $result.Actions += "Import list '$PluginName' already configured: $($updateResult.ChangedFields -join ', ')"
                } else {
                    $result.Actions += "Import list '$PluginName' already configured (no changes)"
                }
            }
        }

        # ==========================================================================
        # Step 6: Legacy Tidalarr split-brain fix (copy from indexer to client)
        # ==========================================================================
        if ($PluginName -eq "Tidalarr") {
            $indexer = Find-ConfiguredComponent -Type "indexer" -PluginName $PluginName
            $client = Find-ConfiguredComponent -Type "downloadclient" -PluginName $PluginName

            if ($indexer -and $client) {
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
                    $result.Actions += "Copied configPath from indexer to download client (split-brain fix)"
                    $needsUpdate = $true
                }

                if (-not [string]::IsNullOrWhiteSpace("$indexerRedirectUrl") -and [string]::IsNullOrWhiteSpace("$clientRedirectUrl")) {
                    $updatedFields = Set-LidarrFieldValue -Fields $updatedFields -Name "redirectUrl" -Value $indexerRedirectUrl
                    $result.Actions += "Copied redirectUrl from indexer to download client (split-brain fix)"
                    $needsUpdate = $true
                }

                if ($needsUpdate) {
                    $clientFull.fields = $updatedFields
                    try {
                        Invoke-LidarrApi -Endpoint ("downloadclient/{0}" -f $clientFull.id) -Method PUT -Body $clientFull | Out-Null
                    }
                    catch {
                        Invoke-LidarrApi -Endpoint "downloadclient" -Method PUT -Body $clientFull | Out-Null
                    }
                }
            }
        }

        $result.Details.preferredIdUsed = ($result.Details.resolution.Values -contains "preferredId")
        Save-E2EComponentIdsIfEnabled -PluginName $PluginName -ComponentIds $result.Details.componentIds

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
        # ImportList gate settings for Brainarr:
        # - configurationUrl is the LLM endpoint (Ollama/LM Studio URL) - treated as a prereq, not a secret
        # - It's "sensitive-ish" (may contain internal IPs) so it's redacted in logs/bundles
        # - Gate SKIPs if configurationUrl is empty (no LLM = can't sync recommendations)
        # - This is Brainarr-specific; other import list plugins may have different credential fields
        ImportListCredentialAllOfFieldNames = @("configurationUrl")
        ImportListCredentialAnyOfFieldNames = @()
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
$runImportList = ($Gate -eq "importlist" -or $Gate -eq "all" -or $Gate -eq "bootstrap")
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

    $gateStart = Get-Date
    $schemaResult = Test-SchemaGate -PluginName $plugin `
        -ExpectIndexer:$config.ExpectIndexer `
        -ExpectDownloadClient:$config.ExpectDownloadClient `
        -ExpectImportList:$config.ExpectImportList
    $gateEnd = Get-Date

    $schemaOutcome = if ($schemaResult.Success) { "success" } else { "failed" }
    $schemaRecord = New-OutcomeResult -Gate "Schema" -PluginName $plugin -Outcome $schemaOutcome -Errors $schemaResult.Errors -StartTime $gateStart -EndTime $gateEnd -Details @{
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

        $gateStart = Get-Date
        $configureResult = Test-ConfigureGateForPlugin -PluginName $plugin -PluginConfig $config -ForceUpdate:$ForceConfigUpdate -PassIfAlreadyConfigured:$ConfigurePassIfAlreadyConfigured
        $gateEnd = Get-Date
        $allResults += New-OutcomeResult -Gate "Configure" -PluginName $plugin -Outcome $configureResult.Outcome -Errors $configureResult.Errors -StartTime $gateStart -EndTime $gateEnd -Details @{
            Actions = $configureResult.Actions
            SkipReason = $configureResult.SkipReason
            componentIds = $configureResult.Details.componentIds
            alreadyConfigured = $configureResult.Details.alreadyConfigured      
            created = $configureResult.Details.created
            updated = $configureResult.Details.updated
            forceUpdateUsed = $configureResult.Details.forceUpdateUsed
            resolution = $configureResult.Details.resolution
            preferredIdUsed = $configureResult.Details.preferredIdUsed
        }

        if ($configureResult.Outcome -eq "skipped") {
            $reason = $configureResult.SkipReason
            if (-not $reason) { $reason = "Skipped by gate policy" }
            Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray
        }
        elseif ($configureResult.Success) {
            if ($configureResult.Actions -and $configureResult.Actions.Count -gt 0) {
                Write-Host "       PASS ($($configureResult.Actions.Count) action(s))" -ForegroundColor Green
                foreach ($action in $configureResult.Actions) {
                    Write-Host "       - $action" -ForegroundColor DarkGreen
                }
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
                $pluginIndexer = Find-ConfiguredComponent -Type "indexer" -PluginName $plugin

                if (-not $pluginIndexer) {
                    $amb = Get-ComponentAmbiguityDetails -Type "indexer" -PluginName $plugin
                    if ($amb.IsAmbiguous) {
                        Write-Host "       FAIL (ambiguous configured indexer selection)" -ForegroundColor Red
                        $allResults += New-OutcomeResult -Gate "Search" -PluginName $plugin -Outcome "failed" -Errors @("Ambiguous configured indexer selection ($($amb.Resolution)): IDs=$($amb.CandidateIds -join ',')") -Details @{
                            Reason = "Ambiguous configured indexer selection"
                            Resolution = $amb.Resolution
                            CandidateIds = $amb.CandidateIds
                        }
                        $overallSuccess = $false
                        $stopNow = $true
                        break
                    }

                    Write-Host "       SKIP (no configured indexer found)" -ForegroundColor Yellow
                    $allResults += New-OutcomeResult -Gate "Search" -PluginName $plugin -Outcome "skipped" -Errors @("No configured indexer found for $plugin") -Details @{
                        Reason = "No configured indexer found"
                    }
                }
                else {
                    # Best-effort: persist component IDs discovered outside Configure gate, but only when resolution is safe.
                    Save-E2EComponentIdsIfEnabled -PluginName $plugin -ComponentIds @{ indexerId = $pluginIndexer.id }

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

                    $gateStart = Get-Date
                    $searchResult = Test-SearchGate -IndexerId $pluginIndexer.id `
                        -SearchQuery $searchQuery `
                        -ExpectedMinResults $expectedMin `
                        -CredentialAllOfFieldNames $credAllOf `
                        -CredentialAnyOfFieldNames $credAnyOf `
                        -SkipIndexerTest:$skipIndexerTest
                    $gateEnd = Get-Date

                    $searchOutcome = if ($searchResult.Outcome) { $searchResult.Outcome } elseif ($searchResult.Success) { "success" } else { "failed" }
                    $allResults += New-OutcomeResult -Gate "Search" -PluginName $plugin -Outcome $searchOutcome -Errors $searchResult.Errors -StartTime $gateStart -EndTime $gateEnd -Details @{
                        IndexerId = $pluginIndexer.id
                        ResultCount = $searchResult.ResultCount
                        SearchQuery = $searchQuery
                        SkipIndexerTest = $skipIndexerTest
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
                $pluginIndexer = Find-ConfiguredComponent -Type "indexer" -PluginName $plugin

                if (-not $pluginIndexer) {
                    $amb = Get-ComponentAmbiguityDetails -Type "indexer" -PluginName $plugin
                    if ($amb.IsAmbiguous) {
                        Write-Host "       FAIL (ambiguous configured indexer selection)" -ForegroundColor Red
                        $allResults += New-OutcomeResult -Gate "AlbumSearch" -PluginName $plugin -Outcome "failed" -Errors @("Ambiguous configured indexer selection ($($amb.Resolution)): IDs=$($amb.CandidateIds -join ',')") -Details @{
                            Reason = "Ambiguous configured indexer selection"
                            Resolution = $amb.Resolution
                            CandidateIds = $amb.CandidateIds
                        }
                        $overallSuccess = $false
                        $stopNow = $true
                        break
                    }

                    Write-Host "       SKIP (no configured indexer found)" -ForegroundColor Yellow
                    $allResults += New-OutcomeResult -Gate "AlbumSearch" -PluginName $plugin -Outcome "skipped" -Errors @("No configured indexer found for $plugin") -Details @{
                        Reason = "No configured indexer found"
                    }
                }
                else {
                    # Best-effort: persist component IDs discovered outside Configure gate, but only when resolution is safe.
                    Save-E2EComponentIdsIfEnabled -PluginName $plugin -ComponentIds @{ indexerId = $pluginIndexer.id }

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

                    $gateStart = Get-Date
                    $albumSearchResult = Test-AlbumSearchGate -IndexerId $pluginIndexer.id `
                        -PluginName $plugin `
                        -TestArtistName $testArtist `
                        -TestAlbumName $testAlbum `
                        -CredentialAllOfFieldNames $credAllOf `
                        -CredentialAnyOfFieldNames $credAnyOf `
                        -SkipIfNoCreds:$true
                    $gateEnd = Get-Date

                    # Store for Grab gate
                    $lastAlbumSearchResult = $albumSearchResult
                    $lastPluginIndexer = $pluginIndexer

                    $outcome = if ($albumSearchResult.Outcome) { $albumSearchResult.Outcome } elseif ($albumSearchResult.Success) { "success" } else { "failed" }
                    $allResults += New-OutcomeResult -Gate "AlbumSearch" -PluginName $plugin -Outcome $outcome -Errors $albumSearchResult.Errors -StartTime $gateStart -EndTime $gateEnd -Details @{
                        IndexerId = $pluginIndexer.id
                        ArtistId = $albumSearchResult.ArtistId
                        AlbumId = $albumSearchResult.AlbumId
                        CommandId = $albumSearchResult.CommandId
                        ReleaseCount = $albumSearchResult.ReleaseCount
                        PluginReleaseCount = $albumSearchResult.PluginReleaseCount
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

                $gateStart = Get-Date
                $grabResult = Test-PluginGrabGate -IndexerId $lastPluginIndexer.id `
                    -PluginName $plugin `
                    -AlbumId $lastAlbumSearchResult.AlbumId `
                    -CredentialAllOfFieldNames $credAllOf `
                    -CredentialAnyOfFieldNames $credAnyOf `
                    -ContainerName $ContainerName `
                    -SkipIfNoCreds:$true
                $gateEnd = Get-Date

                $outcome = if ($grabResult.Outcome) { $grabResult.Outcome } elseif ($grabResult.Success) { "success" } else { "failed" }
                $allResults += New-OutcomeResult -Gate "Grab" -PluginName $plugin -Outcome $outcome -Errors $grabResult.Errors -StartTime $gateStart -EndTime $gateEnd -Details @{
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
                    TotalFilesFound = $grabResult.TotalFilesFound
                    ValidatedFiles = $grabResult.ValidatedFiles
                    SkipReason = $grabResult.SkipReason
                }

                if ($outcome -eq "skipped") {
                    $reason = $grabResult.SkipReason
                    if (-not $reason) { $reason = "Skipped by gate policy" }
                    Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray
                }
                elseif ($grabResult.Success) {
                    Write-Host "       PASS (queued: $($grabResult.ReleaseTitle))" -ForegroundColor Green

                    # Optional: Metadata validation after successful grab
                    if ($ValidateMetadata -and $grabResult.OutputPath -and $ContainerName) {
                        Write-Host "  [4b/4] Metadata Gate (opt-in)..." -ForegroundColor Cyan

                        $metadataResult = Test-MetadataGate `
                            -OutputPath $grabResult.OutputPath `
                            -ContainerName $ContainerName `
                            -MaxFilesToCheck $MetadataFilesToCheck

                        $metaOutcome = if ($metadataResult.Outcome) { $metadataResult.Outcome } elseif ($metadataResult.Success) { "success" } else { "failed" }
                        $allResults += New-OutcomeResult -Gate "Metadata" -PluginName $plugin -Outcome $metaOutcome -Errors $metadataResult.Errors -Details @{
                            OutputPath = $metadataResult.OutputPath
                            TotalFilesChecked = $metadataResult.TotalFilesChecked
                            FilesWithTags = $metadataResult.FilesWithTags
                            ValidatedFiles = $metadataResult.ValidatedFiles | ForEach-Object { $_.Name }
                            MissingTags = $metadataResult.MissingTags
                            SkipReason = $metadataResult.SkipReason
                        }

                        if ($metaOutcome -eq "skipped") {
                            $reason = $metadataResult.SkipReason
                            if (-not $reason) { $reason = "Metadata validation skipped" }
                            Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray
                        }
                        elseif (-not $metadataResult.Success) {
                            Write-Host "       FAIL (metadata validation)" -ForegroundColor Red
                            foreach ($err in $metadataResult.Errors) {
                                Write-Host "       - $err" -ForegroundColor Red
                            }
                            $overallSuccess = $false
                            $stopNow = $true
                            break
                        }
                    }
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

    # Gate 5: ImportList (for import-list-only plugins like Brainarr)
    if ($runImportList) {
        Write-Host "  [5/5] ImportList Gate..." -ForegroundColor Cyan

        if (-not $config.ExpectImportList) {
            Write-Host "       SKIP (no import list expected)" -ForegroundColor DarkGray
            $allResults += New-OutcomeResult -Gate "ImportList" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "No import list expected for plugin"
            }
        }
        else {
            try {
                $importListCredAllOf = @()
                $importListCredAnyOf = @()
                if ($config.ContainsKey("ImportListCredentialAllOfFieldNames")) {
                    $importListCredAllOf = @($config.ImportListCredentialAllOfFieldNames)
                }
                if ($config.ContainsKey("ImportListCredentialAnyOfFieldNames")) {
                    $importListCredAnyOf = @($config.ImportListCredentialAnyOfFieldNames)
                }

                $importList = Find-ConfiguredComponent -Type "importlist" -PluginName $plugin
                if ($importList) {
                    Save-E2EComponentIdsIfEnabled -PluginName $plugin -ComponentIds @{ importListId = $importList.id }
                }
                $importListId = if ($importList) { [int]$importList.id } else { 0 }

                $importListResult = Test-ImportListGate -PluginName $plugin -ImportListId $importListId `
                    -CredentialAllOfFieldNames $importListCredAllOf `
                    -CredentialAnyOfFieldNames $importListCredAnyOf `
                    -SkipIfNoCreds:$true

                $outcome = if ($importListResult.Outcome) { $importListResult.Outcome } elseif ($importListResult.Success) { "success" } else { "failed" }
                $allResults += New-OutcomeResult -Gate "ImportList" -PluginName $plugin -Outcome $outcome -Errors $importListResult.Errors -Details @{
                    ImportListId = $importListResult.ImportListId
                    ImportListName = $importListResult.ImportListName
                    CommandId = $importListResult.CommandId
                    CommandStatus = $importListResult.CommandStatus
                    PostSyncVerified = $importListResult.PostSyncVerified
                    PostSyncError = $importListResult.PostSyncError
                    SkipReason = $importListResult.SkipReason
                }

                if ($outcome -eq "skipped") {
                    $reason = $importListResult.SkipReason
                    if (-not $reason) { $reason = "Skipped by gate policy" }
                    Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray
                }
                elseif ($importListResult.Success) {
                    Write-Host "       PASS (sync completed for $($importListResult.ImportListName))" -ForegroundColor Green
                }
                else {
                    Write-Host "       FAIL" -ForegroundColor Red
                    foreach ($err in $importListResult.Errors) {
                        Write-Host "       - $err" -ForegroundColor Red
                    }
                    $overallSuccess = $false
                    $stopNow = $true
                    break
                }
            }
            catch {
                Write-Host "       ERROR: $_" -ForegroundColor Red
                $allResults += New-OutcomeResult -Gate "ImportList" -PluginName $plugin -Outcome "failed" -Errors @("ImportList gate error: $_")
                $overallSuccess = $false
                $stopNow = $true
                break
            }
        }
    }

    Write-Host ""
}

# =============================================================================
# Brainarr LLM Gate: Opt-in functional test for LLM integration
# Runs only if BRAINARR_LLM_BASE_URL is set or -RunBrainarrLLMGate is specified
# =============================================================================
$runBrainarrLLM = $RunBrainarrLLMGate -and $BrainarrLLMBaseUrl -and ($pluginList -contains 'Brainarr') -and $overallSuccess
if ($runBrainarrLLM) {
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Brainarr LLM Gate (opt-in)" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Testing LLM integration with proof-of-life..." -ForegroundColor White
    Write-Host ""

    Write-Host "  Brainarr LLM..." -ForegroundColor Cyan

    try {
        $brainarrLLMResult = Test-BrainarrLLMGate `
            -LlmBaseUrl $BrainarrLLMBaseUrl `
            -ModelId $BrainarrModelId `
            -StrictMode:$StrictBrainarr `
            -CommandTimeoutSec $BrainarrSyncTimeoutSec

        $brainarrLLMOutcome = $brainarrLLMResult.Outcome
        $allResults += New-OutcomeResult -Gate "BrainarrLLM" -PluginName "Brainarr" `
            -Outcome $brainarrLLMOutcome `
            -Errors $brainarrLLMResult.Errors `
            -Details ([ordered]@{
                llmKind = $brainarrLLMResult.Details.llmKind
                modelsCount = $brainarrLLMResult.Details.modelsCount
                firstModelId = $brainarrLLMResult.Details.firstModelId
                modelIdHash = $brainarrLLMResult.Details.modelIdHash
                endpointRedacted = $brainarrLLMResult.Details.endpointRedacted
                importListId = $brainarrLLMResult.Details.importListId
                commandId = $brainarrLLMResult.Details.commandId
                syncCompleted = $brainarrLLMResult.Details.syncCompleted
                lastSyncError = $brainarrLLMResult.Details.lastSyncError
            })

        if ($brainarrLLMOutcome -eq "skipped") {
            $reason = $brainarrLLMResult.SkipReason
            if (-not $reason) { $reason = "Skipped by gate policy" }
            Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray
        }
        elseif ($brainarrLLMResult.Success) {
            Write-Host "       PASS (LLM: $($brainarrLLMResult.Details.llmKind), $($brainarrLLMResult.Details.modelsCount) model(s))" -ForegroundColor Green
        }
        else {
            Write-Host "       FAIL" -ForegroundColor Red
            foreach ($err in $brainarrLLMResult.Errors) {
                Write-Host "       - $err" -ForegroundColor Red
            }
            $overallSuccess = $false
        }
    }
    catch {
        Write-Host "       ERROR: $_" -ForegroundColor Red
        $allResults += New-OutcomeResult -Gate "BrainarrLLM" -PluginName "Brainarr" -Outcome "failed" -Errors @("BrainarrLLM gate error: $_")
        $overallSuccess = $false
    }

    Write-Host ""
}
elseif ($RunBrainarrLLMGate -and -not $BrainarrLLMBaseUrl) {
    # Gate was requested but no URL provided
    Write-Host ""
    Write-Host "Brainarr LLM Gate... SKIP (BRAINARR_LLM_BASE_URL not set)" -ForegroundColor DarkGray
    $allResults += New-OutcomeResult -Gate "BrainarrLLM" -PluginName "Brainarr" -Outcome "skipped" -Details @{
        Reason = "BRAINARR_LLM_BASE_URL environment variable not set"
    }
    Write-Host ""
}

# Persistency gate is ecosystem-scoped (restarts container once), so run after the main loop.
if ($runPersist -and $overallSuccess) {
    Write-Host "Persist Gate..." -ForegroundColor Cyan
    $gateStart = Get-Date
    $persistResult = Test-PersistGate -PluginList $pluginList -PluginConfigs $pluginConfigs -LidarrUrl $LidarrUrl -ApiKey $effectiveApiKey -ContainerName $ContainerName
    $gateEnd = Get-Date
    $allResults += New-OutcomeResult -Gate "Persist" -PluginName "Ecosystem" -Outcome $persistResult.Outcome -Errors $persistResult.Errors -StartTime $gateStart -EndTime $gateEnd -Details $persistResult.Details

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

# Post-Persist Revalidation: Re-run Search gates after restart to prove functional auth.
# Automatically enabled in bootstrap mode, or explicitly with -PersistRerun.
$runPersistRerun = ($PersistRerun -or $Gate -eq "bootstrap") -and $runPersist -and $overallSuccess
if ($runPersistRerun) {
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Post-Restart Functional Validation" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Re-running Search gates to prove auth works after restart..." -ForegroundColor White
    Write-Host ""

    foreach ($plugin in $pluginList) {
        $config = $pluginConfigs[$plugin]
        if (-not $config) { continue }

        # Skip plugins without indexers (e.g., Brainarr is import-list only)
        if (-not $config.ExpectIndexer) {
            Write-Host "  $plugin Revalidation... SKIP (no indexer)" -ForegroundColor DarkGray
            $allResults += New-OutcomeResult -Gate "Revalidation" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "No indexer expected for plugin"
            }
            continue
        }

        Write-Host "  $plugin Revalidation..." -ForegroundColor Cyan

        try {
            $pluginIndexer = Find-ConfiguredComponent -Type "indexer" -PluginName $plugin

            if (-not $pluginIndexer) {
                Write-Host "       SKIP (no configured indexer found)" -ForegroundColor Yellow
                $allResults += New-OutcomeResult -Gate "Revalidation" -PluginName $plugin -Outcome "skipped" -Details @{
                    Reason = "No configured indexer found post-restart"
                }
                continue
            }

            # Best-effort: persist component IDs discovered outside Configure gate, but only when resolution is safe.
            Save-E2EComponentIdsIfEnabled -PluginName $plugin -ComponentIds @{ indexerId = $pluginIndexer.id }

            # Check for credential file if applicable (same logic as Search gate)
            if ($config.ContainsKey("CredentialFileRelative") -and $config.ContainsKey("CredentialPathField") -and $ContainerName) {
                try {
                    $probePathField = "$($config.CredentialPathField)"
                    $probeConfigPath = Get-LidarrFieldValue -Fields $pluginIndexer.fields -Name $probePathField
                    if (-not [string]::IsNullOrWhiteSpace("$probeConfigPath")) {
                        $relative = "$($config.CredentialFileRelative)"
                        $probeFilePath = "$($probeConfigPath.TrimEnd('/'))/$relative"
                        $probeOk = docker exec $ContainerName sh -c "test -s '$probeFilePath' && echo ok" 2>$null
                        if (-not $probeOk) {
                            Write-Host "       SKIP (credentials file missing post-restart)" -ForegroundColor DarkGray
                            $allResults += New-OutcomeResult -Gate "Revalidation" -PluginName $plugin -Outcome "skipped" -Details @{
                                Reason = "Credentials file missing post-restart"
                                CredentialFile = $probeFilePath
                            }
                            continue
                        }
                    }
                }
                catch { }
            }

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
            if ($config.ContainsKey("CredentialFieldNames")) {
                $credAllOf = @($config.CredentialFieldNames)
            }
            $skipIndexerTest = $true
            if ($config.ContainsKey("SkipIndexerTest")) {
                $skipIndexerTest = [bool]$config.SkipIndexerTest
            }

            $gateStart = Get-Date
            $revalResult = Test-SearchGate -IndexerId $pluginIndexer.id `
                -SearchQuery $searchQuery `
                -ExpectedMinResults $expectedMin `
                -CredentialAllOfFieldNames $credAllOf `
                -CredentialAnyOfFieldNames $credAnyOf `
                -SkipIndexerTest:$skipIndexerTest
            $gateEnd = Get-Date

            $revalOutcome = if ($revalResult.Outcome) { $revalResult.Outcome } elseif ($revalResult.Success) { "success" } else { "failed" }
            $allResults += New-OutcomeResult -Gate "Revalidation" -PluginName $plugin -Outcome $revalOutcome -Errors $revalResult.Errors -StartTime $gateStart -EndTime $gateEnd -Details @{
                IndexerId = $pluginIndexer.id
                ResultCount = $revalResult.ResultCount
                SearchQuery = $searchQuery
                SkipReason = $revalResult.SkipReason
                PostRestart = $true
            }

            if ($revalOutcome -eq "skipped") {
                $reason = $revalResult.SkipReason
                if (-not $reason) { $reason = "Skipped by gate policy" }
                Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray
            }
            elseif ($revalResult.Success) {
                if (-not $skipIndexerTest) {
                    Write-Host "       PASS (indexer/test post-restart)" -ForegroundColor Green
                }
                else {
                    Write-Host "       PASS ($($revalResult.ResultCount) results post-restart)" -ForegroundColor Green
                }
            }
            else {
                Write-Host "       FAIL (auth broken after restart)" -ForegroundColor Red
                foreach ($err in $revalResult.Errors) {
                    Write-Host "       - $err" -ForegroundColor Red
                }
                $overallSuccess = $false
            }
        }
        catch {
            Write-Host "       ERROR: $_" -ForegroundColor Red
            $allResults += New-OutcomeResult -Gate "Revalidation" -PluginName $plugin -Outcome "failed" -Errors @("Revalidation error: $_")
            $overallSuccess = $false
        }
    }
    Write-Host ""
}

# =============================================================================
# Post-Restart Grab Gate: Full grab cycle after restart to prove tokens survive
# Opt-in via -PostRestartGrab or E2E_POST_RESTART_GRAB=1
# =============================================================================
$runPostRestartGrab = $PostRestartGrab -and $Gate -eq "bootstrap" -and $runPersist -and $overallSuccess
if ($runPostRestartGrab) {
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Post-Restart Grab Gate (opt-in)" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Testing full grab cycle after restart to prove tokens survive..." -ForegroundColor White
    Write-Host ""

    $postRestartGrabTimeoutMs = $PostRestartGrabTimeoutMin * 60 * 1000
    $postRestartGrabResults = @{}  # Track output paths for cleanup

    foreach ($plugin in $pluginList) {
        $config = $pluginConfigs[$plugin]
        if (-not $config) { continue }

        # Skip plugins without download clients (e.g., Brainarr)
        if (-not $config.ExpectDownloadClient) {
            Write-Host "  $plugin PostRestartGrab... SKIP (no download client)" -ForegroundColor DarkGray
            $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "skipped" -Details @{
                Reason = "No download client expected for plugin"
            }
            continue
        }

        Write-Host "  $plugin PostRestartGrab..." -ForegroundColor Cyan

        try {
            # Step 1: Find the indexer
            $pluginIndexer = Find-ConfiguredComponent -Type "indexer" -PluginName $plugin

            if (-not $pluginIndexer) {
                Write-Host "       SKIP (no configured indexer found)" -ForegroundColor Yellow
                $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "skipped" -Details @{
                    Reason = "No configured indexer found post-restart"
                }
                continue
            }

            # Best-effort: persist component IDs discovered outside Configure gate, but only when resolution is safe.
            Save-E2EComponentIdsIfEnabled -PluginName $plugin -ComponentIds @{ indexerId = $pluginIndexer.id }

            # Step 2: Check credentials (same logic as other gates)
            $credAllOf = @()
            $credAnyOf = @()
            if ($config.ContainsKey("CredentialAllOfFieldNames")) { $credAllOf = @($config.CredentialAllOfFieldNames) }
            if ($config.ContainsKey("CredentialAnyOfFieldNames")) { $credAnyOf = @($config.CredentialAnyOfFieldNames) }
            if ($config.ContainsKey("CredentialFieldNames")) { $credAllOf = @($config.CredentialFieldNames) }

            # Check for credential file if applicable
            if ($config.ContainsKey("CredentialFileRelative") -and $config.ContainsKey("CredentialPathField") -and $ContainerName) {
                try {
                    $probePathField = "$($config.CredentialPathField)"
                    $probeConfigPath = Get-LidarrFieldValue -Fields $pluginIndexer.fields -Name $probePathField
                    if (-not [string]::IsNullOrWhiteSpace("$probeConfigPath")) {
                        $relative = "$($config.CredentialFileRelative)"
                        $probeFilePath = "$($probeConfigPath.TrimEnd('/'))/$relative"
                        $probeOk = docker exec $ContainerName sh -c "test -s '$probeFilePath' && echo ok" 2>$null
                        if (-not $probeOk) {
                            Write-Host "       SKIP (credentials file missing post-restart)" -ForegroundColor DarkGray
                            $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "skipped" -Details @{
                                Reason = "Credentials file missing post-restart"
                                CredentialFile = $probeFilePath
                            }
                            continue
                        }
                    }
                }
                catch { }
            }

            # Step 3: Re-run AlbumSearch for the same test album
            $testArtist = if ($config.ContainsKey("TestArtistName")) { $config.TestArtistName } else { "Miles Davis" }
            $testAlbum = if ($config.ContainsKey("TestAlbumName")) { $config.TestAlbumName } else { "Kind of Blue" }

            Write-Host "       Searching for releases (post-restart)..." -ForegroundColor Gray

            $albumSearchResult = Test-AlbumSearchGate -IndexerId $pluginIndexer.id `
                -PluginName $plugin `
                -TestArtistName $testArtist `
                -TestAlbumName $testAlbum `
                -CredentialAllOfFieldNames $credAllOf `
                -CredentialAnyOfFieldNames $credAnyOf `
                -SkipIfNoCreds:$true

            if ($albumSearchResult.Outcome -eq "skipped" -or -not $albumSearchResult.Success) {
                $reason = if ($albumSearchResult.SkipReason) { $albumSearchResult.SkipReason } else { "AlbumSearch failed post-restart" }
                Write-Host "       SKIP ($reason)" -ForegroundColor DarkGray
                $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "skipped" -Details @{
                    Reason = $reason
                    AlbumSearchOutcome = $albumSearchResult.Outcome
                }
                continue
            }

            Write-Host "       Found $($albumSearchResult.PluginReleaseCount) releases, selecting one..." -ForegroundColor Gray

            # Step 4: Get releases and select deterministically (first by title asc)
            $releases = Invoke-LidarrApi -Endpoint "release?albumId=$($albumSearchResult.AlbumId)"
            $pluginReleases = $releases | Where-Object {
                $_.indexerId -eq $pluginIndexer.id -or
                [string]::Equals([string]$_.indexer, $plugin, [StringComparison]::OrdinalIgnoreCase)
            } | Sort-Object title | Select-Object -First 1

            if (-not $pluginReleases) {
                Write-Host "       FAIL (no releases from $plugin post-restart)" -ForegroundColor Red
                $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "failed" -Errors @("No releases from $plugin after AlbumSearch post-restart")
                $overallSuccess = $false
                continue
            }

            $selectedRelease = $pluginReleases
            $releaseTitle = if ($selectedRelease.title.Length -gt 50) { $selectedRelease.title.Substring(0,50) + "..." } else { $selectedRelease.title }

            Write-Host "       Selected: $releaseTitle" -ForegroundColor Gray
            Write-Host "       Triggering grab..." -ForegroundColor Gray

            # Step 5: Grab the release
            $grabBody = @{
                guid = $selectedRelease.guid
                indexerId = $selectedRelease.indexerId
            }

            $grabResult = $null
            try {
                $grabResult = Invoke-LidarrApi -Endpoint "release" -Method POST -Body $grabBody
            }
            catch {
                $grabErr = "$_"
                $authPattern = '(?i)(not authenticated|oauth|authorize|token|credential|login|password|api.?key|forbidden|unauthorized|401|403|invalid_grant|invalid_client)'
                if ($grabErr -match $authPattern) {
                    Write-Host "       SKIP (auth error: $grabErr)" -ForegroundColor DarkGray
                    $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "skipped" -Details @{
                        Reason = "Grab failed with auth error post-restart"
                        Error = $grabErr
                    }
                    continue
                }
                Write-Host "       FAIL (grab error: $grabErr)" -ForegroundColor Red
                $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "failed" -Errors @("Grab request failed: $grabErr")
                $overallSuccess = $false
                continue
            }

            if (-not $grabResult -or -not $grabResult.downloadId) {
                Write-Host "       FAIL (grab returned no downloadId)" -ForegroundColor Red
                $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "failed" -Errors @("Grab returned null/empty response or no downloadId")
                $overallSuccess = $false
                continue
            }

            Write-Host "       Grab initiated, downloadId: $($grabResult.downloadId)" -ForegroundColor Green

            # Step 6: Poll queue until completed/failed with timeout
            Write-Host "       Waiting for download to complete (timeout: $PostRestartGrabTimeoutMin min)..." -ForegroundColor Gray
            $deadline = (Get-Date).AddMilliseconds($postRestartGrabTimeoutMs)
            $queueItem = $null
            $downloadCompleted = $false
            $downloadFailed = $false
            $outputPath = $null

            function Get-QueueRecords {
                $queue = Invoke-LidarrApi -Endpoint "queue"
                if ($queue.records) { return $queue.records }
                return @($queue)
            }

            while ((Get-Date) -lt $deadline) {
                $queueRecords = Get-QueueRecords
                $queueItem = $queueRecords | Where-Object { $_.downloadId -eq $grabResult.downloadId } | Select-Object -First 1

                if ($queueItem) {
                    $status = $queueItem.status
                    $trackedState = $queueItem.trackedDownloadState

                    if ($trackedState -eq "importPending" -or $trackedState -eq "imported" -or $status -eq "completed") {
                        $downloadCompleted = $true
                        $outputPath = $queueItem.outputPath
                        Write-Host "       Download completed: $status / $trackedState" -ForegroundColor Green
                        break
                    }
                    elseif ($status -eq "failed" -or $trackedState -eq "downloadFailed" -or $trackedState -eq "importFailed") {
                        $downloadFailed = $true
                        Write-Host "       Download failed: $status / $trackedState" -ForegroundColor Red
                        break
                    }
                    else {
                        # Still in progress
                        $elapsed = [int]((Get-Date) - $deadline.AddMilliseconds(-$postRestartGrabTimeoutMs)).TotalMinutes
                        if ($elapsed % 2 -eq 0) {
                            Write-Host "       Status: $status / $trackedState (${elapsed}m elapsed)..." -ForegroundColor Gray
                        }
                    }
                }

                Start-Sleep -Seconds 5
            }

            if ($downloadFailed) {
                $errorMsg = if ($queueItem.errorMessage) { $queueItem.errorMessage } else { "Download failed" }
                $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "failed" -Errors @($errorMsg) -Details @{
                    SelectedReleaseTitle = $releaseTitle
                    DownloadId = $grabResult.downloadId
                    QueueStatus = $queueItem.status
                    TrackedDownloadState = $queueItem.trackedDownloadState
                }
                $overallSuccess = $false
                continue
            }

            if (-not $downloadCompleted) {
                Write-Host "       FAIL (timeout waiting for download)" -ForegroundColor Red
                $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "failed" -Errors @("Timeout waiting for download to complete") -Details @{
                    SelectedReleaseTitle = $releaseTitle
                    DownloadId = $grabResult.downloadId
                    TimeoutMin = $PostRestartGrabTimeoutMin
                }
                $overallSuccess = $false
                continue
            }

            # Step 7: Validate downloaded files
            Write-Host "       Validating downloaded files..." -ForegroundColor Gray

            if (-not $outputPath) {
                # Try to find outputPath from download client
                $outputPath = "/downloads/$($plugin.ToLower())"
            }

            $fileValidation = Test-AudioFileValidation -OutputPath $outputPath -ContainerName $ContainerName -MaxFilesToCheck 3

            $validatedFileNames = @()
            if ($fileValidation.ValidatedFiles) {
                $validatedFileNames = $fileValidation.ValidatedFiles | ForEach-Object { $_.Name }
            }

            if (-not $fileValidation.Success) {
                $fileErrors = $fileValidation.Errors
                if ($fileValidation.TotalFilesFound -eq 0) {
                    $fileErrors = @("E2E_ZERO_AUDIO_FILES: No audio files found in $outputPath")
                }
                Write-Host "       FAIL (file validation)" -ForegroundColor Red
                foreach ($err in $fileErrors) {
                    Write-Host "       - $err" -ForegroundColor Red
                }
                $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "failed" -Errors $fileErrors -Details @{
                    SelectedReleaseTitle = $releaseTitle
                    DownloadId = $grabResult.downloadId
                    OutputPath = $outputPath
                    ValidatedFiles = $validatedFileNames
                    TotalFilesFound = $fileValidation.TotalFilesFound
                }
                $overallSuccess = $false
                continue
            }

            Write-Host "       PASS ($($fileValidation.ValidatedFiles.Count) files validated)" -ForegroundColor Green

            # Track output path for optional cleanup
            $postRestartGrabResults[$plugin] = @{
                OutputPath = $outputPath
                DownloadId = $grabResult.downloadId
            }

            $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "success" -Details @{
                SelectedReleaseTitle = $releaseTitle
                DownloadId = $grabResult.downloadId
                OutputPath = $outputPath
                ValidatedFiles = $validatedFileNames
                TotalFilesFound = $fileValidation.TotalFilesFound
                QueueStatus = $queueItem.status
                TrackedDownloadState = $queueItem.trackedDownloadState
            }
        }
        catch {
            Write-Host "       ERROR: $_" -ForegroundColor Red
            $allResults += New-OutcomeResult -Gate "PostRestartGrab" -PluginName $plugin -Outcome "failed" -Errors @("PostRestartGrab error: $_")
            $overallSuccess = $false
        }
    }

    # Optional cleanup
    if ($CleanupAfterPostRestartGrab -and $postRestartGrabResults.Count -gt 0) {
        Write-Host ""
        Write-Host "Cleaning up post-restart grab downloads..." -ForegroundColor Gray
        foreach ($plugin in $postRestartGrabResults.Keys) {
            $info = $postRestartGrabResults[$plugin]
            if ($info.OutputPath -and $ContainerName) {
                try {
                    docker exec $ContainerName rm -rf "$($info.OutputPath)" 2>$null
                    Write-Host "  Removed: $($info.OutputPath)" -ForegroundColor Gray
                }
                catch {
                    Write-Host "  Warning: Failed to remove $($info.OutputPath): $_" -ForegroundColor Yellow
                }
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

# Write JSON manifest if requested
if ($EmitJson) {
    try {
        # Determine effective gates based on what was requested
        $effectiveGates = @()
        if ($Gate -eq 'all' -or $Gate -eq 'bootstrap') {
            $effectiveGates = @('Schema', 'Configure', 'Search', 'AlbumSearch', 'Grab', 'ImportList', 'Persist')
            if ($PersistRerun -or $Gate -eq 'bootstrap') {
                $effectiveGates += 'Revalidation'
            }
        } else {
            $effectiveGates = @($Gate)
        }

        # Try to get Lidarr version
        $lidarrVersion = $null
        $lidarrBranch = $null
        try {
            $statusResponse = Invoke-RestMethod -Uri "$LidarrUrl/api/v1/system/status" -Headers @{ 'X-Api-Key' = $effectiveApiKey } -TimeoutSec 5
            $lidarrVersion = $statusResponse.version
            $lidarrBranch = $statusResponse.branch
        } catch { }

        # Try to get container fingerprinting from docker inspect
        $imageTag = $null
        $imageDigest = $null
        $containerId = $null
        $imageId = $null
        $containerStartedAt = $null
        try {
            $inspectJson = docker inspect $ContainerName 2>$null | ConvertFrom-Json
            if ($inspectJson) {
                $imageTag = ($inspectJson.Config.Image -split ':')[-1]
                $imageDigest = $inspectJson.Image
                # Container fingerprinting
                $containerId = if ($inspectJson.Id) { $inspectJson.Id.Substring(0, 12) } else { $null }
                $imageId = $inspectJson.Image
                if ($inspectJson.State -and $inspectJson.State.StartedAt) {
                    try {
                        $containerStartedAt = [DateTime]::Parse($inspectJson.State.StartedAt).ToUniversalTime()
                    } catch { }
                }
            }
        } catch { }

        # Build clean args array (fix #4: avoid backtick pollution from multiline)
        $cleanArgs = @()
        foreach ($key in $PSBoundParameters.Keys) {
            $val = $PSBoundParameters[$key]
            if ($val -is [switch]) {
                if ($val.IsPresent) { $cleanArgs += "-$key" }
            } elseif ($val -is [bool]) {
                if ($val) { $cleanArgs += "-$key" }
            } else {
                $cleanArgs += "-$key"
                $cleanArgs += "$val"
            }
        }

        $jsonContext = @{
            LidarrUrl = $LidarrUrl
            ContainerName = $ContainerName
            ContainerId = $containerId
            ContainerStartedAt = $containerStartedAt
            ImageTag = $imageTag
            ImageId = $imageId
            ImageDigest = $imageDigest
            RequestedGate = $Gate
            Plugins = $pluginList
            EffectiveGates = $effectiveGates
            EffectivePlugins = $pluginList
            RedactionSelfTestExecuted = $true
            RedactionSelfTestPassed = $redactionSelfTestPassed
            RunnerArgs = $cleanArgs
            DiagnosticsBundlePath = if ($bundlePath) { $bundlePath } else { $null }
            LidarrVersion = $lidarrVersion
            LidarrBranch = $lidarrBranch
        }

        New-Item -ItemType Directory -Path (Split-Path $JsonOutputPath -Parent) -Force -ErrorAction SilentlyContinue | Out-Null
        Write-E2ERunManifest -Results $allResults -Context $jsonContext -OutputPath $JsonOutputPath
    }
    catch {
        Write-Host "WARNING: Failed to write JSON manifest: $_" -ForegroundColor Yellow
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
