# E2E Gate Implementations for Plugin Testing
# Gates: Schema (no credentials) -> Search (credentials) -> Grab (credentials)

$script:LidarrApiUrl = $null
$script:LidarrApiKey = $null

# Import shared classifier (single source of truth for credential prereq detection)
$classifierPath = Join-Path $PSScriptRoot "e2e-error-classifier.psm1"
if (Test-Path $classifierPath) {
    Import-Module $classifierPath -Force
}

# Import shared release selection helper (deterministic, culture-invariant)
$releaseSelectionPath = Join-Path $PSScriptRoot "e2e-release-selection.psm1"
if (Test-Path $releaseSelectionPath) {
    Import-Module $releaseSelectionPath -Force
}

# Import shared docker helper (structured E2E_DOCKER_UNAVAILABLE at source)
$dockerPath = Join-Path $PSScriptRoot "e2e-docker.psm1"
if (Test-Path $dockerPath) {
    Import-Module $dockerPath -Force
}

function Initialize-E2EGates {
    <#
    .SYNOPSIS
        Initialize gate module with Lidarr connection details.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ApiUrl,

        [Parameter(Mandatory)]
        [string]$ApiKey
    )

    $script:LidarrApiUrl = $ApiUrl.TrimEnd('/')
    $script:LidarrApiKey = $ApiKey
}

function Invoke-LidarrApi {
    <#
    .SYNOPSIS
        Helper to invoke Lidarr API endpoints.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Endpoint,

        [string]$Method = 'GET',

        [object]$Body = $null,

        [int]$TimeoutSec = 30
    )

    $headers = @{
        'X-Api-Key' = $script:LidarrApiKey
        'Content-Type' = 'application/json'
    }

    $params = @{
        Uri = "$script:LidarrApiUrl/api/v1/$Endpoint"
        Method = $Method
        Headers = $headers
        TimeoutSec = $TimeoutSec
    }

    if ($Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 10)
    }

    $response = Invoke-RestMethod @params
    return $response
}

function Test-PackagingPreflight {
    <#
    .SYNOPSIS
        Preflight check: Validate plugin packages don't contain host-provided DLLs.

    .DESCRIPTION
        Validates that plugin ZIP files or directories do not contain assemblies that
        are provided by the Lidarr host. Shipping host-provided DLLs causes ALC/type-identity
        conflicts like "Method 'Test' does not have an implementation".

        FORBIDDEN DLLs (host-provided / cross-boundary type identity risk):
          - FluentValidation.dll
          - Microsoft.Extensions.DependencyInjection.Abstractions.dll
          - Microsoft.Extensions.Logging.Abstractions.dll
          - System.Text.Json.dll
          - NLog.dll
          - Lidarr.*.dll (host assemblies)
          - NzbDrone.*.dll (host assemblies)

        REQUIRED DLLs:
          - Lidarr.Plugin.<Name>.dll (merged plugin)
          - Lidarr.Plugin.Abstractions.dll (host does NOT provide this)
          - plugin.json
          - Any plugin-specific additional assemblies

    .PARAMETER PluginPath
        Path to a plugin ZIP file or directory to validate.

    .OUTPUTS
        PSCustomObject with Success, ForbiddenDlls, AllowedDlls, Errors
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PluginPath
    )

    $result = [PSCustomObject]@{
        Gate = 'PackagingPreflight'
        PluginPath = $PluginPath
        Success = $false
        ForbiddenDlls = @()
        AllowedDlls = @()
        Errors = @()
    }

    # Canonical list of DLLs that MUST NOT be shipped
    $forbiddenExactDlls = @(
        'FluentValidation.dll',
        'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
        'Microsoft.Extensions.Logging.Abstractions.dll',
        'NLog.dll',
        'System.Text.Json.dll'
    )

    # Host assemblies that must never be shipped in plugin packages
    $forbiddenDllWildcards = @(
        'Lidarr.*.dll',
        'NzbDrone.*.dll'
    )

    $requiredExactDlls = @(
        'Lidarr.Plugin.Abstractions.dll'
    )

    try {
        $dlls = @()

        if (Test-Path -LiteralPath $PluginPath -PathType Container) {
            # Directory - scan for DLLs
            $dlls = Get-ChildItem -LiteralPath $PluginPath -Filter '*.dll' -Recurse | ForEach-Object { $_.Name }
        } elseif ($PluginPath -match '\.zip$' -and (Test-Path -LiteralPath $PluginPath)) {
            # ZIP file - list contents
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [System.IO.Compression.ZipFile]::OpenRead($PluginPath)
            try {
                $dlls = $zip.Entries | Where-Object { $_.Name -match '\.dll$' } | ForEach-Object { $_.Name }
            } finally {
                $zip.Dispose()
            }
        } else {
            $result.Errors += "Plugin path not found or invalid: $PluginPath"
            return $result
        }

        # Check required DLLs first (missing these is always a hard error)
        foreach ($required in $requiredExactDlls) {
            if ($dlls -notcontains $required) {
                $result.Errors += "Missing required DLL: $required"
            }
        }

        $hasPluginAssembly = $dlls | Where-Object { $_ -like 'Lidarr.Plugin.*.dll' -and $_ -ne 'Lidarr.Plugin.Abstractions.dll' } | Select-Object -First 1
        if (-not $hasPluginAssembly) {
            $result.Errors += "Missing plugin assembly (expected a Lidarr.Plugin.<Name>.dll)"
        }

        # Check for forbidden DLLs
        foreach ($dll in $dlls) {
            $isForbidden = $false

            # Plugin assemblies (Lidarr.Plugin.*.dll) are ALLOWED - skip forbidden check for these
            if ($dll -like 'Lidarr.Plugin.*.dll') {
                $result.AllowedDlls += $dll
                continue
            }

            if ($forbiddenExactDlls -contains $dll) {
                $isForbidden = $true
            } else {
                foreach ($pattern in $forbiddenDllWildcards) {
                    if ($dll -like $pattern) {
                        $isForbidden = $true
                        break
                    }
                }
            }

            if ($isForbidden) { $result.ForbiddenDlls += $dll } else { $result.AllowedDlls += $dll }
        }

        if ($result.ForbiddenDlls.Count -gt 0) {
            $result.Errors += "Package contains forbidden DLLs: $($result.ForbiddenDlls -join ', ')"
        }

        if ($result.Errors.Count -gt 0) {
            Write-Host "[PackagingPreflight] FAIL" -ForegroundColor Red
            foreach ($err in $result.Errors) {
                Write-Host "  - $err" -ForegroundColor Red
            }
            if ($result.ForbiddenDlls.Count -gt 0) {
                Write-Host "  Forbidden DLLs:" -ForegroundColor Red
                foreach ($dll in $result.ForbiddenDlls) {
                    Write-Host "    - $dll" -ForegroundColor Red
                }
            }
        } else {
            $result.Success = $true
            Write-Host "[PackagingPreflight] PASS - No forbidden DLLs found" -ForegroundColor Green
            Write-Host "  Allowed DLLs: $($result.AllowedDlls -join ', ')" -ForegroundColor DarkGray
        }
    } catch {
        $result.Errors += "Preflight check failed: $($_.Exception.Message)"
        Write-Host "[PackagingPreflight] ERROR - $($_.Exception.Message)" -ForegroundColor Red
    }

    return $result
}

function Test-SchemaGate {
    <#
    .SYNOPSIS
        Gate 1: Verify plugin schema is registered (requires Lidarr API key).

    .DESCRIPTION
        Checks that the plugin's indexer and/or download client schemas
        are present in Lidarr's schema endpoints. Also supports import list schemas.

        When schemas are missing, emits E2E_SCHEMA_MISSING_IMPLEMENTATION with
        details.discoveryDiagnosis to help distinguish root causes.

    .PARAMETER PluginName
        Name of the plugin (e.g., "Qobuzarr", "Tidalarr", "Brainarr")

    .PARAMETER ExpectIndexer
        Whether to expect an indexer schema.

    .PARAMETER ExpectDownloadClient
        Whether to expect a download client schema.

    .PARAMETER ExpectImportList
        Whether to expect an import list schema.

    .PARAMETER PluginPackagePath
        Optional path to deployed plugin package (zip or folder) for diagnosis.

    .PARAMETER ContainerName
        Optional Docker container name for host capability checks.

    .OUTPUTS
        PSCustomObject with Success, IndexerFound, DownloadClientFound, ImportListFound, Errors, Details
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PluginName,

        [switch]$ExpectIndexer,
        [switch]$ExpectDownloadClient,
        [switch]$ExpectImportList,

        [string]$PluginPackagePath,
        [string]$ContainerName
    )

    function Test-SchemaItemMatchesPlugin {
        param(
            [AllowNull()] $SchemaItem,
            [Parameter(Mandatory)] [string] $ExpectedPluginName
        )

        if ($null -eq $SchemaItem) { return $false }

        $implementationName = $null
        $implementation = $null
        if ($SchemaItem -is [hashtable]) {
            $implementationName = $SchemaItem['implementationName']
            $implementation = $SchemaItem['implementation']
        } else {
            $implementationName = $SchemaItem.implementationName
            $implementation = $SchemaItem.implementation
        }

        if (
            [string]::IsNullOrWhiteSpace([string]$implementationName) -and
            [string]::IsNullOrWhiteSpace([string]$implementation)
        ) {
            return $false
        }

        return (
            [string]::Equals([string]$implementationName, $ExpectedPluginName, [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals([string]$implementation, $ExpectedPluginName, [StringComparison]::OrdinalIgnoreCase)
        )
    }

    function Get-HostEnablePluginsFromContainer {
        param([Parameter(Mandatory)][string]$Name)

        try {
            $xml = docker exec $Name cat /config/config.xml 2>$null
            if ([string]::IsNullOrWhiteSpace($xml)) { return $null }
            $m = [regex]::Match($xml, '<EnablePlugins>\s*(?<v>true|false)\s*</EnablePlugins>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if (-not $m.Success) { return $null }
            return ($m.Groups['v'].Value.Trim().ToLowerInvariant() -eq 'true')
        } catch {
            return $null
        }
    }

    $result = [PSCustomObject]@{
        Gate = 'Schema'
        PluginName = $PluginName
        Success = $false
        IndexerFound = $false
        DownloadClientFound = $false
        ImportListFound = $false
        Errors = @()
        Details = @{
            discoveryDiagnosis = @{
                schemaEndpointReachable = $false
                indexerSchemaCount = 0
                downloadClientSchemaCount = 0
                importListSchemaCount = 0
                pluginImplementationFound = $false
                pluginPackagePresent = $null      # null = not checked, true/false = checked
                pluginJsonPresent = $null
                pluginAssemblyPresent = $null
                hostPluginDiscoveryEnabled = $null # null = unknown, true/false = confirmed
            }
        }
    }

    $diagnosis = $result.Details.discoveryDiagnosis

    try {
        # Track schema accessibility and implementation presence
        $schemasAccessible = $false
        $pluginMissing = $false

        # Best-effort host plugin discovery check (Docker only)
        if (-not [string]::IsNullOrWhiteSpace($ContainerName)) {
            $enablePlugins = Get-HostEnablePluginsFromContainer -Name $ContainerName
            if ($null -ne $enablePlugins) {
                $diagnosis.hostPluginDiscoveryEnabled = [bool]$enablePlugins
            }
        }

        # Check indexer schemas
        if ($ExpectIndexer) {
            $indexerSchemas = Invoke-LidarrApi -Endpoint 'indexer/schema'
            $schemasAccessible = $true
            $diagnosis.schemaEndpointReachable = $true
            $diagnosis.indexerSchemaCount = @($indexerSchemas).Count

            $result.IndexerFound = @($indexerSchemas | Where-Object { Test-SchemaItemMatchesPlugin -SchemaItem $_ -ExpectedPluginName $PluginName }).Count -gt 0

            if (-not $result.IndexerFound) {
                $pluginMissing = $true
                $result.Errors += "Indexer schema for '$PluginName' not found"
            }
        }

        # Check download client schemas
        if ($ExpectDownloadClient) {
            $clientSchemas = Invoke-LidarrApi -Endpoint 'downloadclient/schema'
            $schemasAccessible = $true
            $diagnosis.schemaEndpointReachable = $true
            $diagnosis.downloadClientSchemaCount = @($clientSchemas).Count

            $result.DownloadClientFound = @($clientSchemas | Where-Object { Test-SchemaItemMatchesPlugin -SchemaItem $_ -ExpectedPluginName $PluginName }).Count -gt 0

            if (-not $result.DownloadClientFound) {
                $pluginMissing = $true
                $result.Errors += "DownloadClient schema for '$PluginName' not found"
            }
        }

        # Check import list schemas
        if ($ExpectImportList) {
            $importListSchemas = Invoke-LidarrApi -Endpoint 'importlist/schema'
            $schemasAccessible = $true
            $diagnosis.schemaEndpointReachable = $true
            $diagnosis.importListSchemaCount = @($importListSchemas).Count

            $result.ImportListFound = @($importListSchemas | Where-Object { Test-SchemaItemMatchesPlugin -SchemaItem $_ -ExpectedPluginName $PluginName }).Count -gt 0

            if (-not $result.ImportListFound) {
                $pluginMissing = $true
                $result.Errors += "ImportList schema for '$PluginName' not found"
            }
        }

        # Update diagnosis
        $diagnosis.pluginImplementationFound = -not $pluginMissing

        # Check plugin package presence if path provided
        if ($PluginPackagePath) {
            if (Test-Path $PluginPackagePath) {
                $diagnosis.pluginPackagePresent = $true

                # Check for plugin.json
                if (Test-Path $PluginPackagePath -PathType Container) {
                    $diagnosis.pluginJsonPresent = Test-Path (Join-Path $PluginPackagePath 'plugin.json')
                    $diagnosis.pluginAssemblyPresent = @(Get-ChildItem $PluginPackagePath -Filter '*.dll' -ErrorAction SilentlyContinue).Count -gt 0
                } elseif ($PluginPackagePath -match '\.zip$') {
                    # For zip files, just mark as present (detailed inspection would require extraction)
                    $diagnosis.pluginJsonPresent = $null
                    $diagnosis.pluginAssemblyPresent = $null
                }
            } else {
                $diagnosis.pluginPackagePresent = $false
            }
        }

        # Emit appropriate error code based on diagnosis
        if ($schemasAccessible -and $pluginMissing) {
            $code = 'E2E_SCHEMA_MISSING_IMPLEMENTATION'
            if ($diagnosis.hostPluginDiscoveryEnabled -eq $false) {
                $code = 'E2E_HOST_PLUGIN_DISCOVERY_DISABLED'
            }

            $result.Details.ErrorCode = $code
            $result.Errors += "${code}: Schema endpoint reachable but '$PluginName' implementation not found. See details.discoveryDiagnosis for diagnosis."
        }

        $result.Success = ($result.Errors.Count -eq 0)
    }
    catch {
        $result.Errors += "Schema gate failed: $_"
    }

    return $result
}

function Test-SearchGate {
    <#
    .SYNOPSIS
        Gate 2: Verify plugin can perform a search (credentials required).

    .DESCRIPTION
        Requires a configured indexer. Performs a test search and verifies
        results are returned. Skips the brittle indexer/test endpoint and
        relies on search results as the functional test.

    .PARAMETER IndexerId
        ID of the configured indexer to test.

    .PARAMETER SearchQuery
        Search query to use (defaults to "Kind of Blue Miles Davis")

    .PARAMETER ExpectedMinResults
        Minimum number of results expected (default: 1)

    .PARAMETER SkipIndexerTest
        Skip the POST indexer/test call (default: true, avoids Priority validation issues)

    .OUTPUTS
        PSCustomObject with Success, ResultCount, Errors, RawResponse
    #>
    param(
        [Parameter(Mandatory)]
        [int]$IndexerId,

        [string]$SearchQuery = "Kind of Blue Miles Davis",

        [int]$ExpectedMinResults = 1,

        [switch]$SkipIndexerTest = $true,

        # Backward compatibility: this maps to CredentialAllOfFieldNames.
        [string[]]$CredentialFieldNames = @(),

        [string[]]$CredentialAllOfFieldNames = @(),

        [object[]]$CredentialAnyOfFieldNames = @(),

        [switch]$SkipIfNoCreds = $true
    )

    $result = [PSCustomObject]@{
        Gate = 'Search'
        IndexerId = $IndexerId
        Outcome = 'failed' # success|failed|skipped
        Success = $false
        ResultCount = 0
        SearchQuery = $SearchQuery
        Errors = @()
        RawResponse = $null
        SkipReason = $null
    }

    try {
        # Get indexer info
        $indexer = Invoke-LidarrApi -Endpoint "indexer/$IndexerId"

        if (-not $indexer) {
            $result.Errors += "Indexer $IndexerId not found"
            return $result
        }

        function Has-Field {
            param(
                [AllowNull()]
                $Fields,
                [Parameter(Mandatory)]
                [string]$Name
            )

            if ($null -eq $Fields) { return $false }
            $arr = if ($Fields -is [array]) { $Fields } else { @($Fields) }
            foreach ($f in $arr) {
                $fname = if ($f -is [hashtable]) { $f['name'] } else { $f.name }
                if ([string]::Equals("$fname", $Name, [StringComparison]::OrdinalIgnoreCase)) {
                    return $true
                }
            }
            return $false
        }

        function Get-FieldValue {
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

        function Is-MissingCredentials {
            param(
                [AllowNull()]
                $Indexer,
                [string[]]$AllOfFields,
                [object[]]$AnyOfFields,
                [ref]$MissingReason
            )

            $MissingReason.Value = $null
            $missing = New-Object System.Collections.Generic.List[string]

            foreach ($fieldName in $AllOfFields) {
                if (-not (Has-Field -Fields $Indexer.fields -Name $fieldName)) { continue }
                $value = Get-FieldValue -Fields $Indexer.fields -Name $fieldName
                if ([string]::IsNullOrWhiteSpace("$value")) {
                    $missing.Add($fieldName)
                }
            }

            if ($AnyOfFields -and $AnyOfFields.Count -gt 0) {
                $groups = @()
                foreach ($entry in $AnyOfFields) {
                    if ($null -eq $entry) { continue }
                    if ($entry -is [array]) {
                        $groups += ,@($entry)
                    }
                    else {
                        $groups += ,@(@("$entry"))
                    }
                }

                $anyApplicable = $false
                $anySatisfied = $false

                foreach ($group in $groups) {
                    if (-not $group -or $group.Count -eq 0) { continue }

                    $groupApplicable = $true
                    foreach ($fieldName in $group) {
                        if (-not (Has-Field -Fields $Indexer.fields -Name $fieldName)) {
                            $groupApplicable = $false
                            break
                        }
                    }

                    if (-not $groupApplicable) { continue }
                    $anyApplicable = $true

                    $groupSatisfied = $true
                    foreach ($fieldName in $group) {
                        $value = Get-FieldValue -Fields $Indexer.fields -Name $fieldName
                        if ([string]::IsNullOrWhiteSpace("$value")) {
                            $groupSatisfied = $false
                            break
                        }
                    }

                    if ($groupSatisfied) {
                        $anySatisfied = $true
                        break
                    }
                }

                if ($anyApplicable -and -not $anySatisfied) {
                    $groupDesc = $groups | ForEach-Object { "(" + ($_ -join " + ") + ")" }
                    $missing.Add("any of: " + ($groupDesc -join " OR "))
                }
            }

            if ($missing.Count -eq 0) { return $false }
            $MissingReason.Value = $missing -join "; "
            return $true
        }

        if (-not $CredentialAllOfFieldNames -or $CredentialAllOfFieldNames.Count -eq 0) {
            $CredentialAllOfFieldNames = $CredentialFieldNames
        }

        $missingReason = $null
        if ($SkipIfNoCreds -and (Is-MissingCredentials -Indexer $indexer -AllOfFields $CredentialAllOfFieldNames -AnyOfFields $CredentialAnyOfFieldNames -MissingReason ([ref]$missingReason))) {
            $result.Outcome = 'skipped'
            $result.SkipReason = "Credentials not configured ($missingReason)"
            return $result
        }

        # Optional: Test the indexer. For plugin indexers, this is the most reliable functional
        # gate because it exercises the plugin's configuration/auth validation, and avoids
        # brittle assumptions about Lidarr's global search endpoint behavior.
        if (-not $SkipIndexerTest) {
            # Auto-fix priority if out of range (1-50)
            $priority = $indexer.priority
            if ($priority -lt 1 -or $priority -gt 50) {
                Write-Host "       [WARN] Indexer priority $priority out of range, using 25 for test" -ForegroundColor Yellow
                $priority = 25
            }

            $testBody = @{
                id = $IndexerId
                name = $indexer.name
                implementation = $indexer.implementation
                configContract = $indexer.configContract
                fields = $indexer.fields
                priority = $priority
            }

            $testResult = Invoke-LidarrApi -Endpoint "indexer/test" -Method POST -Body $testBody

            if ($testResult.isValid -eq $false) {
                $validationJson = $testResult.validationFailures | ConvertTo-Json -Compress

                if ($SkipIfNoCreds -and ($validationJson -match '(?i)(not authenticated|oauth|authorize|redirect|token|password|email|required)')) {
                    $result.Outcome = 'skipped'
                    $result.SkipReason = "Indexer test indicates missing/invalid credentials"
                    $result.RawResponse = $validationJson
                    return $result
                }

                $result.Errors += "Indexer test failed: $validationJson"
                return $result
            }

            # If the plugin reports its config is valid, treat Search gate as passed.
            # A full "release search returns results" gate is intentionally separate because
            # it requires library entities + command orchestration (AlbumSearch/Release lookup).
            $result.Success = $true
            $result.Outcome = 'success'
            return $result
        }

        # Fallback: Perform a global search when indexer/test is skipped.
        # Note: this may not exercise a specific indexer; prefer indexer/test above.
        $searchResults = Invoke-LidarrApi -Endpoint "search?term=$([Uri]::EscapeDataString($SearchQuery))"

        # Keep first 3 results for diagnostics, but avoid leaking URLs/tokens.
        $preview = $searchResults | Select-Object -First 3
        try {
            if (Get-Command Invoke-SecretRedaction -ErrorAction SilentlyContinue) {
                $preview = Invoke-SecretRedaction -Object $preview
            }
        }
        catch { }
        try {
            $json = $preview | ConvertTo-Json -Depth 8 -Compress
            $json = [regex]::Replace(
                $json,
                'https?://[^"\\s]+',
                '<redacted-url>',
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
            $result.RawResponse = $json
        }
        catch {
            $result.RawResponse = $null
        }

        $result.ResultCount = ($searchResults | Measure-Object).Count
        $result.Success = $result.ResultCount -ge $ExpectedMinResults
        $result.Outcome = if ($result.Success) { 'success' } else { 'failed' }

        if (-not $result.Success) {
            $result.Errors += "Search returned $($result.ResultCount) results (expected >= $ExpectedMinResults) for '$SearchQuery'"
        }
    }
    catch {
        $result.Errors += "Search gate failed: $_"
    }

    return $result
}

function Test-IsCredentialPrereqSkipReason {
    <#
    .SYNOPSIS
        Detects whether a skip reason is due to missing/invalid credentials.
    .DESCRIPTION
        Used by the runner to optionally convert "skipped" credential prereqs into failures
        under strict CI modes (e.g., STRICT_E2E=1).
    #>
    param(
        [AllowNull()]
        [string]$SkipReason
    )

    if ([string]::IsNullOrWhiteSpace($SkipReason)) { return $false }

    if (Get-Command -Name Get-E2EErrorClassification -ErrorAction SilentlyContinue) {
        $classification = Get-E2EErrorClassification -Messages @($SkipReason)
        return [bool]$classification.isCredentialPrereq
    }

    # Fallback (shouldn't happen in normal usage): keep previous behavior if module import fails.
    return $SkipReason -match '(?i)(credentials not configured|missing env vars|missing/invalid credentials|not authenticated|auth error|invalid_grant|invalid_client|unauthorized|forbidden|401|403|credential(s)? file missing)'
}

function Test-GrabGate {
    <#
    .SYNOPSIS
        Gate 3: Verify plugin can grab/download (credentials required).

    .DESCRIPTION
        Requires a configured download client. Initiates a download
        and verifies it appears in the queue.

    .PARAMETER DownloadClientId
        ID of the configured download client.

    .PARAMETER ReleaseGuid
        GUID of the release to grab (from search results).

    .OUTPUTS
        PSCustomObject with Success, QueueItemId, Errors
    #>
    param(
        [Parameter(Mandatory)]
        [int]$DownloadClientId,

        [Parameter(Mandatory)]
        [string]$ReleaseGuid
    )

    $result = [PSCustomObject]@{
        Gate = 'Grab'
        DownloadClientId = $DownloadClientId
        Success = $false
        QueueItemId = $null
        Details = @{}
        Errors = @()
    }

    try {
        # Get download client info
        $client = Invoke-LidarrApi -Endpoint "downloadclient/$DownloadClientId"

        if (-not $client) {
            $result.Errors += "Download client $DownloadClientId not found"
            return $result
        }

        # Test the download client
        $testResult = Invoke-LidarrApi -Endpoint "downloadclient/test" -Method POST -Body @{
            id = $DownloadClientId
            name = $client.name
            implementation = $client.implementation
            configContract = $client.configContract
            fields = $client.fields
        }

        if ($testResult.isValid -eq $false) {
            $result.Errors += "Download client test failed: $($testResult.validationFailures | ConvertTo-Json)"
            return $result
        }

        # Grab the release
        $grabResult = Invoke-LidarrApi -Endpoint "release" -Method POST -Body @{
            guid = $ReleaseGuid
            indexerId = 0  # Will use the release's indexer
        }

        # Check queue for the item
        Start-Sleep -Seconds 2
        $queue = Invoke-LidarrApi -Endpoint "queue"
        $queueItem = $queue.records | Where-Object { $_.downloadId -eq $grabResult.downloadId }

        if ($queueItem) {
            $result.QueueItemId = $queueItem.id
            $result.Success = $true
        }
        else {
            $result.Errors += "Grab succeeded but item not found in queue"
            $result.Details.ErrorCode = 'E2E_QUEUE_NOT_FOUND'
            $result.Details.queueTimeoutSec = 2
            $result.Details.downloadId = $grabResult.downloadId
        }
    }
    catch {
        $result.Errors += "Grab gate failed: $_"
    }

    return $result
}

function Test-PluginGrabGate {
    <#
    .SYNOPSIS
        Gate 4: Verify plugin release can be grabbed and queued.

    .DESCRIPTION
        Builds on AlbumSearch gate results:
        1. Fetches releases for the specified album
        2. Finds a plugin-attributed release (by indexerId or name)
        3. Triggers a grab via POST /release
        4. Verifies item appears in queue

        This is credential-gated: skips if no releases from plugin,
        fails if grab or queue verification fails.

    .PARAMETER IndexerId
        ID of the configured indexer to match releases.

    .PARAMETER PluginName
        Name of the plugin (for fallback matching).

    .PARAMETER AlbumId
        Album ID to fetch releases for (from AlbumSearch gate).

    .PARAMETER CredentialFieldNames
        Field names to check for credentials (for skip logic).

    .PARAMETER SkipIfNoCreds
        Skip gate if credentials are missing.

    .OUTPUTS
        PSCustomObject with Success, Outcome, ReleaseGuid, QueueItemId, Errors
    #>
    param(
        [Parameter(Mandatory)]
        [int]$IndexerId,

        [Parameter(Mandatory)]
        [string]$PluginName,

        [Parameter(Mandatory)]
        [int]$AlbumId,

        # Backward compatibility: this maps to CredentialAllOfFieldNames.
        [string[]]$CredentialFieldNames = @(),

        [string[]]$CredentialAllOfFieldNames = @(),

        [object[]]$CredentialAnyOfFieldNames = @(),

        [int]$QueueTimeoutSec = 60,

        [int]$CompletionTimeoutSec = 600,

        [switch]$ValidateOutputPath = $true,

        [string]$ContainerName = $null,

        # v3: Multi-file validation parameters
        [int]$MaxFilesToValidate = 5,

        [int]$MinBytesPerFile = 65536,  # 64KB - catches HTML/JSON errors

        [int]$ExpectedMinFiles = 1,

        [switch]$SkipIfNoCreds = $true
    )

    $currentPhase = 'Grab:Start'

    $result = [PSCustomObject]@{
        Gate = 'Grab'
        PluginName = $PluginName
        IndexerId = $IndexerId
        IndexerName = $null
        IndexerImplementation = $null
        AlbumId = $AlbumId
        Outcome = 'failed'
        Success = $false
        ReleaseGuid = $null
        ReleaseTitle = $null
        QueueItemId = $null
        DownloadId = $null
        OutputPath = $null
        QueueStatus = $null
        TrackedDownloadStatus = $null
        TrackedDownloadState = $null
        # v3: Multi-file validation results
        TotalFileCount = 0
        ValidatedFileCount = 0
        ValidatedFiles = @()
        ValidationFailures = @()
        SampleFile = $null  # Kept for backward compat (first validated file)
        Errors = @()
        Details = @{}
        SkipReason = $null
    }

    try {
        $currentPhase = 'Grab:GetIndexerInfo'

        # Get indexer info for diagnostics
        $indexer = Invoke-LidarrApi -Endpoint "indexer/$IndexerId"       
        if ($indexer) {
            $result.IndexerName = $indexer.name
            $result.IndexerImplementation = $indexer.implementation      
        }

        # Credential check helpers
        function Get-FieldValue {
            param($Fields, [string]$Name)
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

        function Has-Field {
            param($Fields, [string]$Name)
            if ($null -eq $Fields) { return $false }
            $arr = if ($Fields -is [array]) { $Fields } else { @($Fields) }
            foreach ($f in $arr) {
                $fname = if ($f -is [hashtable]) { $f['name'] } else { $f.name }
                if ([string]::Equals("$fname", $Name, [StringComparison]::OrdinalIgnoreCase)) {
                    return $true
                }
            }
            return $false
        }

        if (-not $CredentialAllOfFieldNames -or $CredentialAllOfFieldNames.Count -eq 0) {
            $CredentialAllOfFieldNames = $CredentialFieldNames
        }

        # Check credentials if required
        if ($SkipIfNoCreds -and $indexer) {
            $missing = New-Object System.Collections.Generic.List[string]

            foreach ($fieldName in $CredentialAllOfFieldNames) {
                if (-not (Has-Field -Fields $indexer.fields -Name $fieldName)) { continue }
                $value = Get-FieldValue -Fields $indexer.fields -Name $fieldName
                if ([string]::IsNullOrWhiteSpace("$value")) {
                    $missing.Add($fieldName)
                }
            }

            if ($CredentialAnyOfFieldNames -and $CredentialAnyOfFieldNames.Count -gt 0) {
                $groups = @()
                foreach ($entry in $CredentialAnyOfFieldNames) {
                    if ($null -eq $entry) { continue }
                    if ($entry -is [array]) {
                        $groups += ,@($entry)
                    }
                    else {
                        $groups += ,@(@("$entry"))
                    }
                }

                $anyApplicable = $false
                $anySatisfied = $false

                foreach ($group in $groups) {
                    if (-not $group -or $group.Count -eq 0) { continue }

                    $groupApplicable = $true
                    foreach ($fieldName in $group) {
                        if (-not (Has-Field -Fields $indexer.fields -Name $fieldName)) {
                            $groupApplicable = $false
                            break
                        }
                    }
                    if (-not $groupApplicable) { continue }
                    $anyApplicable = $true

                    $groupSatisfied = $true
                    foreach ($fieldName in $group) {
                        $value = Get-FieldValue -Fields $indexer.fields -Name $fieldName
                        if ([string]::IsNullOrWhiteSpace("$value")) {
                            $groupSatisfied = $false
                            break
                        }
                    }
                    if ($groupSatisfied) {
                        $anySatisfied = $true
                        break
                    }
                }

                if ($anyApplicable -and -not $anySatisfied) {
                    $groupDesc = $groups | ForEach-Object { "(" + ($_ -join " + ") + ")" }
                    $missing.Add("any of: " + ($groupDesc -join " OR "))
                }
            }

            if ($missing.Count -gt 0) {
                $result.Outcome = 'skipped'
                $result.SkipReason = "Credentials not configured ($($missing -join '; '))"
                return $result
            }
        }

        # Step 1: Fetch releases for album
        Write-Host "       Fetching releases for album $AlbumId..." -ForegroundColor Gray
        $releases = Invoke-LidarrApi -Endpoint "release?albumId=$AlbumId"
        $totalReleases = ($releases | Measure-Object).Count

        if ($totalReleases -eq 0) {
            # Log album identity so it's clear this isn't an attribution bug
            $indexerContext = "name='$($result.IndexerName)' impl='$($result.IndexerImplementation)' id=$IndexerId"
            Write-Host "       No releases found for album $AlbumId (indexer: $indexerContext)" -ForegroundColor Yellow
            Write-Host "       This is expected if album has no releases cached - not an attribution issue" -ForegroundColor DarkGray
            Write-Host "       Note: If you ran -Gate grab alone, run -Gate all to trigger AlbumSearch first" -ForegroundColor DarkGray
            $result.Outcome = 'skipped'
            $result.SkipReason = "No releases available for album $AlbumId (indexer: $indexerContext). If running -Gate grab alone, use -Gate all to run AlbumSearch first."
            return $result
        }

        Write-Host "       Total releases: $totalReleases" -ForegroundColor Gray

        # Step 2: Find plugin-attributed release
        $pluginReleases = $releases | Where-Object {
            $_.indexerId -eq $IndexerId -or
            [string]::Equals($_.indexer, $PluginName, [StringComparison]::OrdinalIgnoreCase)
        }

        $pluginReleaseCount = ($pluginReleases | Measure-Object).Count
        Write-Host "       Releases from ${PluginName}: $pluginReleaseCount" -ForegroundColor $(if ($pluginReleaseCount -gt 0) { 'Green' } else { 'Red' })

        if ($pluginReleaseCount -eq 0) {
            # Releases exist but none attributed to plugin - this is a FAIL (attribution regression)
            # Same diagnostics style as AlbumSearch gate
            $indexerContext = "name='$($result.IndexerName)' impl='$($result.IndexerImplementation)' id=$IndexerId"
            $summary = Get-FoundIndexerNamesDetails -Releases $releases
            $foundDisplay = if ($summary.foundIndexerNameCount -eq 0) {
                '(none)'
            } else {
                ($summary.foundIndexerNames -join ', ')
            }
            if ($summary.foundIndexerNamesCapped) {
                $more = $summary.foundIndexerNameCount - $summary.foundIndexerNames.Count
                if ($more -gt 0) { $foundDisplay += " (+$more more)" }
            }

            Write-Host "       FAIL: No releases attributed to plugin!" -ForegroundColor Red
            $result.Errors += "No releases from configured indexer [$indexerContext]. Total: $totalReleases. Found indexers: $foundDisplay"

            # Structured details for E2E_NO_RELEASES_ATTRIBUTED (bounded + machine-readable)
            $result.Details.ErrorCode = 'E2E_NO_RELEASES_ATTRIBUTED'
            $result.Details.totalReleases = $totalReleases
            $result.Details.attributedReleases = $pluginReleaseCount
            $result.Details.expectedIndexerName = $PluginName
            $result.Details.expectedIndexerId = $IndexerId
            $result.Details.foundIndexerNames = @($summary.foundIndexerNames)
            $result.Details.foundIndexerNameCount = $summary.foundIndexerNameCount
            $result.Details.foundIndexerNamesCapped = [bool]$summary.foundIndexerNamesCapped

            # Check for null-indexer releases (same as AlbumSearch gate)
            $nullIndexerReleases = $releases | Where-Object {
                [string]::IsNullOrWhiteSpace($_.indexer) -or $_.indexerId -eq 0
            }
            $nullIndexerCount = ($nullIndexerReleases | Measure-Object).Count
            $result.Details.nullIndexerReleaseCount = $nullIndexerCount

            if ($nullIndexerCount -gt 0) {
                Write-Host "       WARNING: $nullIndexerCount releases have null/empty indexer or indexerId=0!" -ForegroundColor Red
                $result.Errors += "ATTRIBUTION WARNING: $nullIndexerCount of $totalReleases releases have null/empty indexer or indexerId=0"

                # Structured samples (up to 3) for machine-readable diagnostics
                $result.Details.nullIndexerSamples = @($nullIndexerReleases | Select-Object -First 3 | ForEach-Object {
                    $title = if ($_.title -and $_.title.Length -gt 40) { $_.title.Substring(0,40) + "..." } else { $_.title }
                    @{
                        title = $title
                        indexer = $_.indexer
                        indexerId = $_.indexerId
                    }
                })

                # Human-readable samples in Errors[]
                foreach ($sample in $result.Details.nullIndexerSamples) {
                    $sampleStr = "indexer='$($sample.indexer)' indexerId=$($sample.indexerId) title='$($sample.title)'"
                    $result.Errors += "  Null-indexer sample: $sampleStr"
                    Write-Host "       - $sampleStr" -ForegroundColor Yellow
                }
            }
            else {
                $result.Details.nullIndexerSamples = @()
            }

            # Outcome is FAIL, not SKIP - this is a regression
            return $result
        }

        $currentPhase = 'Grab:SelectRelease'

        # Pick release deterministically (culture-invariant, stable sort)
        $selection = Select-DeterministicRelease -Releases $pluginReleases -ReturnSelectionBasis
        $targetRelease = $selection.release
        $result.ReleaseGuid = $targetRelease.guid
        $result.ReleaseTitle = if ($targetRelease.title.Length -gt 50) { $targetRelease.title.Substring(0,50) + "..." } else { $targetRelease.title }
        $result.Details.SelectionBasis = $selection.selectionBasis

        Write-Host "       Selected: $($result.ReleaseTitle) (of $($selection.selectionBasis.candidateCount) candidates)" -ForegroundColor Gray
        Write-Host "       Triggering grab..." -ForegroundColor Gray     

        $currentPhase = 'Grab:GrabRequest'

        # Step 3: Grab the release
        $grabBody = @{
            guid = $targetRelease.guid
            indexerId = $targetRelease.indexerId
        }

        $grabResult = $null
        try {
            $grabResult = Invoke-LidarrApi -Endpoint "release" -Method POST -Body $grabBody
        }
        catch {
            $grabErr = "$_"
            # Auth pattern: only these specific conditions are credential issues → SKIP
            # Includes OAuth token exchange failures (invalid_grant, invalid_client)
            # 5xx, other 4xx, or generic errors indicate API/host drift → FAIL
            $authPattern = '(?i)(not authenticated|oauth|authorize|token|credential|login|password|api.?key|forbidden|unauthorized|401|403|invalid_grant|invalid_client)'
            if ($grabErr -match $authPattern) {
                $result.Outcome = 'skipped'
                $result.SkipReason = "Grab failed with auth error: $grabErr"
                return $result
            }
            # Non-auth error (5xx, bad payload, endpoint drift) → let it FAIL
            $result.Errors += "Grab request failed: $grabErr"
            return $result
        }

        if (-not $grabResult) {
            $result.Errors += "Grab returned null/empty response"
            return $result
        }

        $result.DownloadId = $grabResult.downloadId
        Write-Host "       Grab initiated, downloadId: $($grabResult.downloadId)" -ForegroundColor Green

        function Get-QueueRecords {
            $queue = Invoke-LidarrApi -Endpoint "queue"
            if ($queue.records) { return $queue.records }
            return @($queue)
        }

        # Step 4: Wait for queue item
        $currentPhase = 'Grab:WaitQueueItem'
        Write-Host "       Waiting for queue item..." -ForegroundColor Gray
        $deadline = (Get-Date).AddSeconds([Math]::Max(1, $QueueTimeoutSec))
        $queueItem = $null
        $grabTime = Get-Date

        while ((Get-Date) -lt $deadline) {
            $queueRecords = Get-QueueRecords

            # Try matching by downloadId first (if available)
            if ($grabResult.downloadId) {
                $queueItem = $queueRecords | Where-Object { $_.downloadId -eq $grabResult.downloadId } | Select-Object -First 1
            }

            # Fallback: match by albumId + indexer + recent timestamp (within 2 min of grab)
            if (-not $queueItem) {
                $queueItem = $queueRecords | Where-Object {
                    $_.albumId -eq $AlbumId -and
                    $_.indexer -eq $result.IndexerName -and
                    ([DateTime]::Parse($_.added) -gt $grabTime.AddMinutes(-2))
                } | Sort-Object { [DateTime]::Parse($_.added) } -Descending | Select-Object -First 1
            }

            if ($queueItem) { break }
            Start-Sleep -Milliseconds 500
        }

        if (-not $queueItem) {
            $queueRecords = Get-QueueRecords
            $queueCount = ($queueRecords | Measure-Object).Count
            $result.Errors += "Grab succeeded but item not found in queue (waited ${QueueTimeoutSec}s; queue has $queueCount items)"
            $result.Errors += "downloadId=$($grabResult.downloadId), albumId=$AlbumId, indexer=$($result.IndexerName)"

            # Fail-fast: queue contract failure should be explicit and stable (not inferred from text)
            $result.Details.ErrorCode = 'E2E_QUEUE_NOT_FOUND'
            $result.Details.queueTimeoutSec = $QueueTimeoutSec
            $result.Details.queueCount = $queueCount
            $result.Details.downloadId = $grabResult.downloadId
            $result.Details.albumId = $AlbumId
            $result.Details.indexerName = $result.IndexerName
            return $result
        }

        $result.QueueItemId = $queueItem.id
        $result.OutputPath = $queueItem.outputPath
        $result.QueueStatus = $queueItem.status
        $result.TrackedDownloadStatus = $queueItem.trackedDownloadStatus
        $result.TrackedDownloadState = $queueItem.trackedDownloadState

        Write-Host "       Queue item found: id=$($queueItem.id) status=$($queueItem.status)" -ForegroundColor Green

        # Step 5: Wait for completion
        $currentPhase = 'Grab:WaitQueueCompletion'
        $completionDeadline = (Get-Date).AddSeconds([Math]::Max(1, $CompletionTimeoutSec))
        while ((Get-Date) -lt $completionDeadline) {
            $queueRecords = Get-QueueRecords
            $current = $queueRecords | Where-Object { $_.id -eq $result.QueueItemId } | Select-Object -First 1
            if (-not $current) { break }

            $result.QueueStatus = $current.status
            $result.TrackedDownloadStatus = $current.trackedDownloadStatus
            $result.TrackedDownloadState = $current.trackedDownloadState
            $result.OutputPath = $current.outputPath

            if ([string]::Equals($current.status, "completed", [StringComparison]::OrdinalIgnoreCase)) { break }
            if ([string]::Equals($current.status, "failed", [StringComparison]::OrdinalIgnoreCase)) { break }

            Start-Sleep -Seconds 2
        }

        if ([string]::Equals($result.QueueStatus, "failed", [StringComparison]::OrdinalIgnoreCase)) {
            $result.Errors += "Queue item failed (downloadId=$($grabResult.downloadId)): $($queueItem.errorMessage)"
            return $result
        }

        if (-not [string]::Equals($result.QueueStatus, "completed", [StringComparison]::OrdinalIgnoreCase)) {
            $result.Errors += "Queue item did not complete within ${CompletionTimeoutSec}s (status=$($result.QueueStatus), downloadId=$($grabResult.downloadId))"

            # Structured timeout details (explicit at source)
            $timeoutDetails = New-ApiTimeoutDetails `
                -TimeoutType 'queueCompletion' `
                -TimeoutSeconds $CompletionTimeoutSec `
                -Endpoint "/api/v1/queue" `
                -Operation 'GrabQueueWait' `
                -PluginName $PluginName `
                -Phase 'Grab:WaitQueueCompletion' `
                -IndexerId $IndexerId
            foreach ($key in $timeoutDetails.Keys) {
                $result.Details[$key] = $timeoutDetails[$key]
            }

            return $result
        }

        # Step 6: v3 multi-file validation (requires Docker container access)
        if ($ValidateOutputPath -and $ContainerName -and $result.OutputPath) {
            $currentPhase = 'Grab:OutputValidation'

            # Docker prereq: explicit E2E_DOCKER_UNAVAILABLE at source (no classifier inference)
            if (Get-Command Test-E2EDockerAvailable -ErrorAction SilentlyContinue) {
                $dockerCheck = Test-E2EDockerAvailable -Phase 'Grab:ValidateOutputPath' -Operation 'docker exec'
                if (-not $dockerCheck.Success) {
                    $result.Details.ErrorCode = 'E2E_DOCKER_UNAVAILABLE'
                    foreach ($key in $dockerCheck.Details.Keys) { $result.Details[$key] = $dockerCheck.Details[$key] }
                    $result.Errors += "E2E_DOCKER_UNAVAILABLE: $($dockerCheck.Details.Suggestion)"
                    return $result
                }
            }
            try {
                $container = $ContainerName.Trim()
                $outPath = $result.OutputPath

                # Check directory exists
                $dirOk = docker exec $container sh -c "test -d '$outPath' && echo ok" 2>$null
                if ((@($dirOk) -join '').Trim() -ne 'ok') {
                    $result.Errors += "Output path not found in container: $outPath"
                    return $result
                }

                # Get all files with sizes (format: "size path")
                $fileListRaw = docker exec $container sh -c "find '$outPath' -type f -exec stat -c '%s %n' {} \; 2>/dev/null || find '$outPath' -type f -exec ls -l {} \; 2>/dev/null | awk '{print \$5, \$9}'" 2>$null
                $fileLines = @($fileListRaw) -split "`n" | Where-Object { $_.Trim() }

                $allFiles = @()
                foreach ($line in $fileLines) {
                    $parts = $line.Trim() -split '\s+', 2
                    if ($parts.Count -ge 2) {
                        $size = 0
                        if ([int]::TryParse($parts[0], [ref]$size)) {
                            $allFiles += [PSCustomObject]@{
                                Path = $parts[1]
                                Size = $size
                                Name = Split-Path -Leaf $parts[1]
                            }
                        }
                    }
                }

                $result.TotalFileCount = $allFiles.Count
                Write-Host "       Files in outputPath: $($allFiles.Count) ($($allFiles.Count - ($allFiles | Where-Object { $_.Name.EndsWith('.partial') }).Count) completed)" -ForegroundColor Gray

                # Filter out .partial files for minimum count check
                $completedForCount = $allFiles | Where-Object { -not $_.Name.EndsWith('.partial') }

                # Check minimum file count (only completed files)
                if ($completedForCount.Count -lt $ExpectedMinFiles) {
                    $result.Errors += "Expected at least $ExpectedMinFiles completed files, found $($completedForCount.Count) under: $outPath"
                    # Dump file list for diagnostics
                    $fileListDiag = $allFiles | Select-Object -First 20 | ForEach-Object { "$($_.Name) ($($_.Size) bytes)" }
                    if ($fileListDiag) {
                        $result.Errors += "Files found: $($fileListDiag -join '; ')"
                    }
                    return $result
                }

                # Helper: read magic bytes from container file
                function Get-ContainerFileMagicHex {
                    param([string]$Container, [string]$FilePath, [int]$ByteCount = 12)
                    $hex = $null
                    try {
                        $pythonCode = "import sys; p=sys.argv[1]; b=open(p,'rb').read($ByteCount); print(b.hex())"
                        $hex = docker exec $Container python3 -c $pythonCode $FilePath 2>$null
                        $hex = (@($hex) -join '').Trim().ToLowerInvariant()
                        if ($hex) { return $hex }
                    } catch { }
                    try {
                        $hex = docker exec $Container xxd -p -l $ByteCount $FilePath 2>$null
                        $hex = (@($hex) -join '').Trim().ToLowerInvariant().Replace("`n", "").Replace("`r", "")
                        if ($hex) { return $hex }
                    } catch { }
                    try {
                        $hex = docker exec $Container hexdump -v -n $ByteCount -e '1/1 "%02x"' $FilePath 2>$null
                        $hex = (@($hex) -join '').Trim().ToLowerInvariant()
                        if ($hex) { return $hex }
                    } catch { }
                    try {
                        $hex = docker exec $Container od -An -tx1 -N$ByteCount $FilePath 2>$null
                        $hex = ((@($hex) -join ' ') -replace '\s', '').Trim().ToLowerInvariant()
                        if ($hex) { return $hex }
                    } catch { }
                    return $null
                }

                # Helper: check if magic bytes indicate audio
                function Test-AudioMagic {
                    param([string]$Magic)
                    if (-not $Magic -or $Magic.Length -lt 4) { return $false }
                    $isFlac = $Magic.StartsWith("664c6143")  # fLaC
                    $isOgg = $Magic.StartsWith("4f676753")   # OggS
                    $isRiff = $Magic.StartsWith("52494646")  # RIFF (WAV)
                    $isId3 = $Magic.StartsWith("494433")     # ID3 (MP3)
                    $isFtyp = ($Magic.Length -ge 16) -and ($Magic.Substring(8, 8) -eq "66747970")  # ftyp (M4A/MP4)
                    $isMpeg = $Magic.StartsWith("fffb") -or $Magic.StartsWith("fff3") -or $Magic.StartsWith("fff2")  # MPEG audio
                    return ($isFlac -or $isOgg -or $isRiff -or $isId3 -or $isFtyp -or $isMpeg)
                }

                # Filter out .partial files (in-progress downloads)
                $completedFiles = $allFiles | Where-Object { -not $_.Name.EndsWith('.partial') }

                # Select files to validate (first N for deterministic debugging)
                $filesToValidate = $completedFiles | Select-Object -First $MaxFilesToValidate
                $validatedFiles = @()
                $validationFailures = @()

                foreach ($file in $filesToValidate) {
                    $validation = [PSCustomObject]@{
                        Path = $file.Path
                        Name = $file.Name
                        Size = $file.Size
                        Magic = $null
                        IsAudio = $false
                        FailReason = $null
                    }

                    # Check size floor
                    if ($file.Size -lt $MinBytesPerFile) {
                        $validation.FailReason = "File too small: $($file.Size) bytes (min: $MinBytesPerFile)"
                        $validationFailures += $validation
                        continue
                    }

                    # Read and validate magic bytes
                    $magic = Get-ContainerFileMagicHex -Container $container -FilePath $file.Path -ByteCount 12
                    $validation.Magic = $magic

                    if (-not $magic) {
                        $validation.FailReason = "Failed to read magic bytes"
                        $validationFailures += $validation
                        continue
                    }

                    $isAudio = Test-AudioMagic -Magic $magic
                    $validation.IsAudio = $isAudio

                    if (-not $isAudio) {
                        $validation.FailReason = "Not audio (magic: $magic)"
                        $validationFailures += $validation
                        continue
                    }

                    $validatedFiles += $validation
                }

                $result.ValidatedFileCount = $validatedFiles.Count
                $result.ValidatedFiles = $validatedFiles | ForEach-Object { $_.Name }
                $result.ValidationFailures = $validationFailures | ForEach-Object { "$($_.Name): $($_.FailReason)" }

                # Set SampleFile for backward compat
                if ($validatedFiles.Count -gt 0) {
                    $result.SampleFile = $validatedFiles[0].Path
                }

                Write-Host "       Validated: $($validatedFiles.Count)/$($filesToValidate.Count) files" -ForegroundColor $(if ($validationFailures.Count -eq 0) { 'Green' } else { 'Yellow' })

                # Fail if any validation failures
                if ($validationFailures.Count -gt 0) {
                    $result.Errors += "File validation failed for $($validationFailures.Count) of $($filesToValidate.Count) checked files"
                    foreach ($failure in $validationFailures) {
                        $result.Errors += "  FAIL: $($failure.Name) - $($failure.FailReason)"
                        Write-Host "       FAIL: $($failure.Name) - $($failure.FailReason)" -ForegroundColor Red

                        # Dump first 64 bytes hex for failed files (diagnostics)
                        if ($failure.Magic) {
                            Write-Host "             Magic (first 12 bytes): $($failure.Magic)" -ForegroundColor DarkGray
                        }
                    }

                    # List all files for context
                    $fileSummary = $allFiles | Select-Object -First 20 | ForEach-Object { "$($_.Name) ($($_.Size)b)" }
                    $result.Errors += "All files (first 20): $($fileSummary -join ', ')"

                    return $result
                }

                # All validated files passed
                Write-Host "       All $($validatedFiles.Count) validated files are valid audio" -ForegroundColor Green
            }
            catch {
                $result.Errors += "Output validation failed: $_"
                return $result
            }
        }

        $result.Success = $true
        $result.Outcome = 'success'
    }
    catch {
        $errMsg = "$_"
        # Check if exception is auth-related
        if ($errMsg -match '(?i)(not authenticated|oauth|authorize|token|credential|login|password|api.?key|forbidden|unauthorized|401|403|invalid_grant|invalid_client)') {
            $result.Outcome = 'skipped'
            $result.SkipReason = "Grab gate failed with auth error: $errMsg"
            return $result
        }
        $result.Errors += "Grab gate failed: $errMsg"
        # Unexpected exceptions should be explicit and stable for triage (not inferred from errors[])
        $result.Details.ErrorCode = 'E2E_INTERNAL_ERROR'
        $result.Details.phase = $currentPhase
        $result.Details.reason = 'UnhandledException'
        $result.Details.note = 'Unhandled exception in Grab gate (see errors[] for redacted exception context).'
    }

    return $result
}

function Test-AlbumSearchGate {
    <#
    .SYNOPSIS
        Gate 2.5 (Medium): Verify plugin returns releases for AlbumSearch command.

    .DESCRIPTION
        This is a more thorough search gate that:
        1. Finds or creates a test artist/album in Lidarr
        2. Triggers AlbumSearch command
        3. Waits for command completion
        4. Verifies releases appear from the specified indexer

        This proves the indexer is actually returning parseable releases,
        not just that it responds to test requests.

    .PARAMETER IndexerId
        ID of the configured indexer to test.

    .PARAMETER PluginName
        Name of the plugin (for filtering releases by indexer).

    .PARAMETER TestArtistName
        Artist name to search for (default: "Miles Davis")

    .PARAMETER TestAlbumName
        Album name to search for (default: "Kind of Blue")

    .PARAMETER CommandTimeoutSec
        Timeout for AlbumSearch command (default: 60)

    .PARAMETER CredentialFieldNames
        Field names to check for credentials (for skip logic).

    .PARAMETER SkipIfNoCreds
        Skip gate if credentials are missing.

    .OUTPUTS
        PSCustomObject with Success, ReleaseCount, Errors, etc.
    #>
    param(
        [Parameter(Mandatory)]
        [int]$IndexerId,

        [Parameter(Mandatory)]
        [string]$PluginName,

        [string]$TestArtistName = "Miles Davis",
        [string]$TestAlbumName = "Kind of Blue",

        [int]$CommandTimeoutSec = 60,

        # Backward compatibility: this maps to CredentialAllOfFieldNames.
        [string[]]$CredentialFieldNames = @(),

        [string[]]$CredentialAllOfFieldNames = @(),

        [object[]]$CredentialAnyOfFieldNames = @(),

        [switch]$SkipIfNoCreds = $true
    )

    $result = [PSCustomObject]@{
        Gate = 'AlbumSearch'
        PluginName = $PluginName
        IndexerId = $IndexerId
        IndexerName = $null
        IndexerImplementation = $null
        Outcome = 'failed'
        Success = $false
        ArtistId = $null
        AlbumId = $null
        CommandId = $null
        ReleaseCount = 0
        PluginReleaseCount = 0
        Errors = @()
        Details = @{}
        SkipReason = $null
    }

    try {
        # Get indexer info
        $indexer = Invoke-LidarrApi -Endpoint "indexer/$IndexerId"

        if (-not $indexer) {
            $result.Errors += "Indexer $IndexerId not found"
            return $result
        }

        # Store indexer details for diagnostics
        $result.IndexerName = $indexer.name
        $result.IndexerImplementation = $indexer.implementation

        # Check for credentials
        function Get-FieldValue {
            param($Fields, [string]$Name)
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

        function Has-Field {
            param($Fields, [string]$Name)
            if ($null -eq $Fields) { return $false }
            $arr = if ($Fields -is [array]) { $Fields } else { @($Fields) }
            foreach ($f in $arr) {
                $fname = if ($f -is [hashtable]) { $f['name'] } else { $f.name }
                if ([string]::Equals("$fname", $Name, [StringComparison]::OrdinalIgnoreCase)) {
                    return $true
                }
            }
            return $false
        }

        if (-not $CredentialAllOfFieldNames -or $CredentialAllOfFieldNames.Count -eq 0) {
            $CredentialAllOfFieldNames = $CredentialFieldNames
        }

        if ($SkipIfNoCreds) {
            $missing = New-Object System.Collections.Generic.List[string]

            foreach ($fieldName in $CredentialAllOfFieldNames) {
                if (-not (Has-Field -Fields $indexer.fields -Name $fieldName)) { continue }
                $value = Get-FieldValue -Fields $indexer.fields -Name $fieldName
                if ([string]::IsNullOrWhiteSpace("$value")) {
                    $missing.Add($fieldName)
                }
            }

            if ($CredentialAnyOfFieldNames -and $CredentialAnyOfFieldNames.Count -gt 0) {
                $groups = @()
                foreach ($entry in $CredentialAnyOfFieldNames) {
                    if ($null -eq $entry) { continue }
                    if ($entry -is [array]) {
                        $groups += ,@($entry)
                    }
                    else {
                        $groups += ,@(@("$entry"))
                    }
                }

                $anyApplicable = $false
                $anySatisfied = $false

                foreach ($group in $groups) {
                    if (-not $group -or $group.Count -eq 0) { continue }

                    $groupApplicable = $true
                    foreach ($fieldName in $group) {
                        if (-not (Has-Field -Fields $indexer.fields -Name $fieldName)) {
                            $groupApplicable = $false
                            break
                        }
                    }
                    if (-not $groupApplicable) { continue }
                    $anyApplicable = $true

                    $groupSatisfied = $true
                    foreach ($fieldName in $group) {
                        $value = Get-FieldValue -Fields $indexer.fields -Name $fieldName
                        if ([string]::IsNullOrWhiteSpace("$value")) {
                            $groupSatisfied = $false
                            break
                        }
                    }
                    if ($groupSatisfied) {
                        $anySatisfied = $true
                        break
                    }
                }

                if ($anyApplicable -and -not $anySatisfied) {
                    $groupDesc = $groups | ForEach-Object { "(" + ($_ -join " + ") + ")" }
                    $missing.Add("any of: " + ($groupDesc -join " OR "))
                }
            }

            if ($missing.Count -gt 0) {
                $result.Outcome = 'skipped'
                $result.SkipReason = "Credentials not configured ($($missing -join '; '))"
                return $result
            }
        }

        # Step 1: Find or create artist
        Write-Host "       Looking up artist: $TestArtistName" -ForegroundColor Gray
        $artists = Invoke-LidarrApi -Endpoint "artist"
        $artist = $artists | Where-Object { $_.artistName -like "*$TestArtistName*" } | Select-Object -First 1

        if (-not $artist) {
            # Search for artist in MusicBrainz via Lidarr
            Write-Host "       Artist not found, searching MusicBrainz..." -ForegroundColor Gray
            $searchResults = Invoke-LidarrApi -Endpoint "artist/lookup?term=$([Uri]::EscapeDataString($TestArtistName))"
            $mbArtist = $searchResults | Select-Object -First 1

            if (-not $mbArtist) {
                $result.Errors += "Cannot find artist '$TestArtistName' in MusicBrainz"
                return $result
            }

            # Add artist to Lidarr (minimal config)
            Write-Host "       Adding artist to Lidarr: $($mbArtist.artistName)" -ForegroundColor Gray
            $rootFolders = Invoke-LidarrApi -Endpoint "rootfolder"
            $rootFolder = $rootFolders | Select-Object -First 1

            if (-not $rootFolder) {
                $result.Errors += "No root folder configured in Lidarr"
                return $result
            }

            $qualityProfiles = Invoke-LidarrApi -Endpoint "qualityprofile"
            $qualityProfile = $qualityProfiles | Select-Object -First 1

            $metadataProfiles = Invoke-LidarrApi -Endpoint "metadataprofile"
            $metadataProfile = $metadataProfiles | Select-Object -First 1

            $addArtistBody = @{
                foreignArtistId = $mbArtist.foreignArtistId
                artistName = $mbArtist.artistName
                qualityProfileId = $qualityProfile.id
                metadataProfileId = $metadataProfile.id
                rootFolderPath = $rootFolder.path
                monitored = $true
                addOptions = @{
                    monitor = "all"
                    searchForMissingAlbums = $false
                }
            }

            $artist = Invoke-LidarrApi -Endpoint "artist" -Method POST -Body $addArtistBody
            Write-Host "       Artist added with ID: $($artist.id)" -ForegroundColor Green
        }

        $result.ArtistId = $artist.id

        # Step 2: Find album
        Write-Host "       Looking up album: $TestAlbumName" -ForegroundColor Gray
        $albums = Invoke-LidarrApi -Endpoint "album?artistId=$($artist.id)"
        $album = $albums | Where-Object { $_.title -like "*$TestAlbumName*" } | Select-Object -First 1

        if (-not $album) {
            # Just pick the first album if specific one not found
            $album = $albums | Select-Object -First 1
            if (-not $album) {
                $result.Errors += "No albums found for artist $($artist.artistName)"
                return $result
            }
            Write-Host "       Using album: $($album.title)" -ForegroundColor Gray
        }

        $result.AlbumId = $album.id
        Write-Host "       Album ID: $($album.id) - $($album.title)" -ForegroundColor Gray

        # Step 3: Trigger AlbumSearch command
        Write-Host "       Triggering AlbumSearch command..." -ForegroundColor Gray
        $commandBody = @{
            name = "AlbumSearch"
            albumIds = @($album.id)
        }

        $command = Invoke-LidarrApi -Endpoint "command" -Method POST -Body $commandBody
        $result.CommandId = $command.id
        Write-Host "       Command ID: $($command.id)" -ForegroundColor Gray

        # Step 4: Wait for command completion
        $startTime = Get-Date
        $timeout = $startTime.AddSeconds($CommandTimeoutSec)
        $completed = $false

        while ((Get-Date) -lt $timeout -and -not $completed) {
            Start-Sleep -Seconds 2
            $commandStatus = Invoke-LidarrApi -Endpoint "command/$($command.id)"

            if ($commandStatus.status -eq 'completed') {
                $completed = $true
                Write-Host "       Command completed" -ForegroundColor Green
            }
            elseif ($commandStatus.status -eq 'failed') {
                $failMsg = $commandStatus.message
                # Check if failure is auth/config related - should SKIP not FAIL
                if ($failMsg -match '(?i)(not authenticated|oauth|authorize|token|credential|login|password|api.?key|forbidden|unauthorized|401|403|invalid_grant|invalid_client)') {
                    $result.Outcome = 'skipped'
                    $result.SkipReason = "AlbumSearch command failed with auth/config error: $failMsg"
                    return $result
                }
                $result.Errors += "AlbumSearch command failed: $failMsg"
                return $result
            }
            else {
                Write-Host "       Command status: $($commandStatus.status)..." -ForegroundColor Gray
            }
        }

        if (-not $completed) {
            # Timeout diagnostics: capture last known state
            $lastStatus = $null
            try { $lastStatus = Invoke-LidarrApi -Endpoint "command/$($command.id)" } catch {}
            $statusInfo = if ($lastStatus) { "status=$($lastStatus.status), message=$($lastStatus.message)" } else { "unknown" }
            $result.Errors += "AlbumSearch command timed out after ${CommandTimeoutSec}s ($statusInfo)"

            # Structured timeout details (explicit at source)
            $timeoutDetails = New-ApiTimeoutDetails `
                -TimeoutType 'commandPoll' `
                -TimeoutSeconds $CommandTimeoutSec `
                -Endpoint "/api/v1/command/$($command.id)" `
                -Operation 'AlbumSearch' `
                -PluginName $PluginName `
                -Phase 'AlbumSearch:PollCommand' `
                -IndexerId $IndexerId `
                -CommandId $command.id
            foreach ($key in $timeoutDetails.Keys) {
                $result.Details[$key] = $timeoutDetails[$key]
            }

            return $result
        }

        # Step 5: Check releases for this album
        Write-Host "       Fetching releases for album $($album.id)..." -ForegroundColor Gray
        $releases = Invoke-LidarrApi -Endpoint "release?albumId=$($album.id)" -TimeoutSec 120

        $result.ReleaseCount = ($releases | Measure-Object).Count
        Write-Host "       Total releases: $($result.ReleaseCount)" -ForegroundColor Gray

        # Filter releases from our indexer
        # Primary: exact indexerId match (most reliable)
        # Fallback: case-insensitive exact indexer name match
        $pluginReleases = $releases | Where-Object {
            $_.indexerId -eq $IndexerId -or
            [string]::Equals($_.indexer, $PluginName, [StringComparison]::OrdinalIgnoreCase)
        }

        $result.PluginReleaseCount = ($pluginReleases | Measure-Object).Count
        Write-Host "       Releases from ${PluginName}: $($result.PluginReleaseCount)" -ForegroundColor $(if ($result.PluginReleaseCount -gt 0) { 'Green' } else { 'Yellow' })

        if ($result.PluginReleaseCount -gt 0) {
            $result.Success = $true
            $result.Outcome = 'success'
        }
        else {
            # Zero releases from plugin - gather diagnostics
            $totalReleases = ($releases | Measure-Object).Count
            $summary = Get-FoundIndexerNamesDetails -Releases $releases
            $foundDisplay = if ($summary.foundIndexerNameCount -eq 0) {
                '(none)'
            } else {
                ($summary.foundIndexerNames -join ', ')
            }
            if ($summary.foundIndexerNamesCapped) {
                $more = $summary.foundIndexerNameCount - $summary.foundIndexerNames.Count
                if ($more -gt 0) { $foundDisplay += " (+$more more)" }
            }

            # Include full indexer context for triage (no extra API call needed)
            $indexerContext = "name='$($result.IndexerName)' impl='$($result.IndexerImplementation)' id=$IndexerId"
            $result.Errors += "No releases from configured indexer [$indexerContext]. Total: $totalReleases. Found indexers: $foundDisplay"

            # Structured details for E2E_NO_RELEASES_ATTRIBUTED (bounded + machine-readable)
            $result.Details.ErrorCode = 'E2E_NO_RELEASES_ATTRIBUTED'
            $result.Details.totalReleases = $totalReleases
            $result.Details.attributedReleases = $result.PluginReleaseCount
            $result.Details.expectedIndexerName = $PluginName
            $result.Details.expectedIndexerId = $IndexerId
            $result.Details.foundIndexerNames = @($summary.foundIndexerNames)
            $result.Details.foundIndexerNameCount = $summary.foundIndexerNameCount
            $result.Details.foundIndexerNamesCapped = [bool]$summary.foundIndexerNamesCapped

            # Check for null-indexer releases - likely parser/attribution regression
            $nullIndexerReleases = $releases | Where-Object {
                [string]::IsNullOrWhiteSpace($_.indexer) -or $_.indexerId -eq 0
            }
            $nullIndexerCount = ($nullIndexerReleases | Measure-Object).Count
            $result.Details.nullIndexerReleaseCount = $nullIndexerCount

            if ($nullIndexerCount -gt 0) {
                # LOUD warning - this is almost always a parser bug
                Write-Host "       WARNING: $nullIndexerCount releases have null/empty indexer or indexerId=0!" -ForegroundColor Red
                Write-Host "       This likely indicates a parser attribution regression." -ForegroundColor Red

                $result.Errors += "ATTRIBUTION WARNING: $nullIndexerCount of $totalReleases releases have null/empty indexer or indexerId=0"

                # Structured samples (up to 3) for machine-readable diagnostics
                $result.Details.nullIndexerSamples = @($nullIndexerReleases | Select-Object -First 3 | ForEach-Object {
                    $title = if ($_.title -and $_.title.Length -gt 40) { $_.title.Substring(0,40) + "..." } else { $_.title }
                    @{
                        title = $title
                        indexer = $_.indexer
                        indexerId = $_.indexerId
                    }
                })

                # Human-readable samples in Errors[]
                foreach ($sample in $result.Details.nullIndexerSamples) {
                    $sampleStr = "indexer='$($sample.indexer)' indexerId=$($sample.indexerId) title='$($sample.title)'"
                    $result.Errors += "  Null-indexer sample: $sampleStr"
                    Write-Host "       - $sampleStr" -ForegroundColor Yellow
                }
            }
            else {
                $result.Details.nullIndexerSamples = @()
            }

            # Sample of properly-attributed releases for comparison (redacted)
            $attributedReleases = $releases | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_.indexer) -and $_.indexerId -ne 0
            } | Select-Object -First 1
            if ($attributedReleases) {
                $title = if ($attributedReleases.title -and $attributedReleases.title.Length -gt 40) { $attributedReleases.title.Substring(0,40) + "..." } else { $attributedReleases.title }
                # Only include safe fields (no guid/downloadUrl/infoUrl)
                $result.Errors += "Sample attributed release: indexer='$($attributedReleases.indexer)' indexerId=$($attributedReleases.indexerId) title='$title'"
            }
        }
    }
    catch {
        $errMsg = "$_"
        # Check if catch is auth-related (includes OAuth token exchange failures)
        if ($errMsg -match '(?i)(not authenticated|oauth|authorize|token|credential|login|password|api.?key|forbidden|unauthorized|401|403|invalid_grant|invalid_client)') {
            $result.Outcome = 'skipped'
            $result.SkipReason = "AlbumSearch gate failed with auth error: $errMsg"
            return $result
        }
        $result.Errors += "AlbumSearch gate failed: $errMsg"
    }

    return $result
}

function Test-ImportListGate {
    <#
    .SYNOPSIS
        Gate for ImportList plugins: Verify import list can sync.

    .DESCRIPTION
        For plugins like Brainarr that provide ImportList functionality:
        1. Detects Brainarr schema presence via GET /api/v1/importlist/schema
        2. Finds an existing configured import list by implementation/name
        3. If no import list configured → Outcome=skipped
        4. If configured but provider credentials missing → Outcome=skipped
        5. If configured and creds present:
           - Triggers ImportListSync command
           - Polls until completed/failed/timeout
           - Verifies sync had effect (command completed successfully)
        6. Returns Outcome=success when proof-of-life satisfied

    .PARAMETER PluginName
        Name of the plugin (e.g., "Brainarr")

    .PARAMETER CommandTimeoutSec
        Timeout for ImportListSync command (default: 120)

    .PARAMETER CredentialAllOfFieldNames
        Field names that must ALL have values for credentials to be considered present.

    .PARAMETER CredentialAnyOfFieldNames
        Field name groups where at least ONE group must have all values present.

    .PARAMETER SkipIfNoCreds
        Skip gate if credentials are missing.

    .OUTPUTS
        PSCustomObject with Outcome, ImportListId, CommandId, Errors, SkipReason
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PluginName,

        # Optional: prefer a known configured import list ID (stable + deterministic).
        # When provided, the gate will try to use this ID first and fall back to discovery if not found.
        [int]$ImportListId = 0,

        [int]$CommandTimeoutSec = 120,

        [string[]]$CredentialAllOfFieldNames = @(),

        [object[]]$CredentialAnyOfFieldNames = @(),

        [switch]$SkipIfNoCreds = $true
    )

    $result = [PSCustomObject]@{
        Gate = 'ImportList'
        PluginName = $PluginName
        Outcome = 'failed'
        Success = $false
        ImportListId = $null
        ImportListName = $null
        ImportListImplementation = $null
        CommandId = $null
        CommandStatus = $null
        PostSyncVerified = $false
        PostSyncError = $null
        Errors = @()
        SkipReason = $null
    }

    try {
        # Step 1: Check schema presence
        $schemas = Invoke-LidarrApi -Endpoint "importlist/schema"
        $pluginSchema = $schemas | Where-Object {
            $_.implementation -like "*$PluginName*" -or
            $_.implementationName -like "*$PluginName*"
        } | Select-Object -First 1

        if (-not $pluginSchema) {
            $result.Outcome = 'skipped'
            $result.SkipReason = "No import list schema found for '$PluginName'"
            return $result
        }

        # Step 2: Find configured import list
        $importLists = Invoke-LidarrApi -Endpoint "importlist"

        $configuredList = $null
        if ($ImportListId -gt 0) {
            try {
                $configuredList = Invoke-LidarrApi -Endpoint "importlist/$ImportListId"
            }
            catch {
                # Fall back to discovery when the stored/preferred ID no longer exists.
                $configuredList = $null
            }
        }

        if (-not $configuredList) {
            # Avoid user-controlled name matching; prefer implementation/implementationName.
            $configuredList = $importLists | Where-Object {
                [string]::Equals([string]$_.implementationName, $PluginName, [StringComparison]::OrdinalIgnoreCase) -or
                ([string]$_.implementation -like "*$PluginName*")
            } | Select-Object -First 1
        }

        if (-not $configuredList) {
            $result.Outcome = 'skipped'
            $result.SkipReason = "No configured import list found for '$PluginName' (schema exists but not configured)"
            return $result
        }

        $result.ImportListId = $configuredList.id
        $result.ImportListName = $configuredList.name
        $result.ImportListImplementation = $configuredList.implementation

        # Get full import list details
        $importListFull = Invoke-LidarrApi -Endpoint "importlist/$($configuredList.id)"

        # Credential check helpers (same pattern as other gates)
        function Has-Field {
            param($Fields, [string]$Name)
            if ($null -eq $Fields) { return $false }
            $arr = if ($Fields -is [array]) { $Fields } else { @($Fields) }
            foreach ($f in $arr) {
                $fname = if ($f -is [hashtable]) { $f['name'] } else { $f.name }
                if ([string]::Equals("$fname", $Name, [StringComparison]::OrdinalIgnoreCase)) {
                    return $true
                }
            }
            return $false
        }

        function Get-FieldValue {
            param($Fields, [string]$Name)
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

        # Step 3: Check credentials
        if ($SkipIfNoCreds) {
            $missing = New-Object System.Collections.Generic.List[string]

            foreach ($fieldName in $CredentialAllOfFieldNames) {
                if (-not (Has-Field -Fields $importListFull.fields -Name $fieldName)) { continue }
                $value = Get-FieldValue -Fields $importListFull.fields -Name $fieldName
                if ([string]::IsNullOrWhiteSpace("$value")) {
                    $missing.Add($fieldName)
                }
            }

            if ($CredentialAnyOfFieldNames -and $CredentialAnyOfFieldNames.Count -gt 0) {
                $groups = @()
                foreach ($entry in $CredentialAnyOfFieldNames) {
                    if ($null -eq $entry) { continue }
                    if ($entry -is [array]) {
                        $groups += ,@($entry)
                    }
                    else {
                        $groups += ,@(@("$entry"))
                    }
                }

                $anyApplicable = $false
                $anySatisfied = $false

                foreach ($group in $groups) {
                    if (-not $group -or $group.Count -eq 0) { continue }

                    $groupApplicable = $true
                    foreach ($fieldName in $group) {
                        if (-not (Has-Field -Fields $importListFull.fields -Name $fieldName)) {
                            $groupApplicable = $false
                            break
                        }
                    }
                    if (-not $groupApplicable) { continue }
                    $anyApplicable = $true

                    $groupSatisfied = $true
                    foreach ($fieldName in $group) {
                        $value = Get-FieldValue -Fields $importListFull.fields -Name $fieldName
                        if ([string]::IsNullOrWhiteSpace("$value")) {
                            $groupSatisfied = $false
                            break
                        }
                    }
                    if ($groupSatisfied) {
                        $anySatisfied = $true
                        break
                    }
                }

                if ($anyApplicable -and -not $anySatisfied) {
                    $groupDesc = $groups | ForEach-Object { "(" + ($_ -join " + ") + ")" }
                    $missing.Add("any of: " + ($groupDesc -join " OR "))
                }
            }

            if ($missing.Count -gt 0) {
                $result.Outcome = 'skipped'
                $result.SkipReason = "Credentials not configured ($($missing -join '; '))"
                return $result
            }
        }

        # Step 4: Capture baseline state before sync
        $preSync = $null
        $preSyncImportListFound = $false
        try {
            $preSync = Invoke-LidarrApi -Endpoint "importlist/$($configuredList.id)"
            $preSyncImportListFound = ($null -ne $preSync)
        }
        catch { }

        # Step 5: Trigger ImportListSync command
        # Note: ImportListSync syncs ALL import lists globally; definitionId is not supported.
        # We target diagnostics at the specific list we're testing.
        Write-Host "       Triggering ImportListSync (global sync, testing list id=$($configuredList.id) name='$($configuredList.name)')..." -ForegroundColor Gray
        $commandBody = @{
            name = "ImportListSync"
        }

        $command = Invoke-LidarrApi -Endpoint "command" -Method POST -Body $commandBody

        # Site A: Check command creation succeeded
        if (-not $command -or -not $command.id) {
            $result.Errors += "Failed to trigger ImportListSync command"

            # Structured import failed details (explicit at source)
            $failedDetails = New-ImportFailedDetails `
                -PluginName $PluginName `
                -ImportListId $configuredList.id `
                -Phase 'ImportList:TriggerCommand' `
                -Endpoint '/api/v1/command' `
                -ImportListName $configuredList.name `
                -PostSyncVerified $false
            foreach ($key in $failedDetails.Keys) {
                $result.Details[$key] = $failedDetails[$key]
            }

            return $result
        }

        $result.CommandId = $command.id
        Write-Host "       Command ID: $($command.id)" -ForegroundColor Gray

        # Step 6: Wait for command completion
        $startTime = Get-Date
        $timeout = $startTime.AddSeconds($CommandTimeoutSec)
        $completed = $false

        while ((Get-Date) -lt $timeout -and -not $completed) {
            Start-Sleep -Seconds 2
            $commandStatus = Invoke-LidarrApi -Endpoint "command/$($command.id)"
            $result.CommandStatus = $commandStatus.status

            if ($commandStatus.status -eq 'completed') {
                $completed = $true
                Write-Host "       Command completed" -ForegroundColor Green
            }
            elseif ($commandStatus.status -eq 'failed') {
                $failMsg = $commandStatus.message
                # Check if failure is auth/config related
                if ($failMsg -match '(?i)(not authenticated|oauth|authorize|token|credential|login|password|api.?key|forbidden|unauthorized|401|403|invalid_grant|invalid_client)') {
                    $result.Outcome = 'skipped'
                    $result.SkipReason = "ImportListSync command failed with auth/config error: $failMsg"
                    return $result
                }
                $result.Errors += "ImportListSync command failed: $failMsg"

                # Site B: Structured import failed details (explicit at source)
                $failedDetails = New-ImportFailedDetails `
                    -PluginName $PluginName `
                    -ImportListId $configuredList.id `
                    -Phase 'ImportList:PollCommand' `
                    -Endpoint "/api/v1/command/$($command.id)" `
                    -ImportListName $configuredList.name `
                    -CommandId $command.id `
                    -CommandStatus $commandStatus.status `
                    -PostSyncVerified $false
                foreach ($key in $failedDetails.Keys) {
                    $result.Details[$key] = $failedDetails[$key]
                }

                return $result
            }
            elseif ($commandStatus.status -eq 'aborted') {
                $result.Errors += "ImportListSync command aborted"

                # Site B: Aborted also counts as import failed
                $failedDetails = New-ImportFailedDetails `
                    -PluginName $PluginName `
                    -ImportListId $configuredList.id `
                    -Phase 'ImportList:PollCommand' `
                    -Endpoint "/api/v1/command/$($command.id)" `
                    -ImportListName $configuredList.name `
                    -CommandId $command.id `
                    -CommandStatus 'aborted' `
                    -PostSyncVerified $false
                foreach ($key in $failedDetails.Keys) {
                    $result.Details[$key] = $failedDetails[$key]
                }

                return $result
            }
            else {
                Write-Host "       Command status: $($commandStatus.status)..." -ForegroundColor Gray
            }
        }

        if (-not $completed) {
            $lastStatus = $null
            try { $lastStatus = Invoke-LidarrApi -Endpoint "command/$($command.id)" } catch {}
            $statusInfo = if ($lastStatus) { "status=$($lastStatus.status), message=$($lastStatus.message)" } else { "unknown" }
            $result.Errors += "ImportListSync command timed out after ${CommandTimeoutSec}s ($statusInfo)"

            # Structured timeout details (explicit at source)
            $timeoutDetails = New-ApiTimeoutDetails `
                -TimeoutType 'commandPoll' `
                -TimeoutSeconds $CommandTimeoutSec `
                -Endpoint "/api/v1/command/$($command.id)" `
                -Operation 'ImportListSync' `
                -PluginName $PluginName `
                -Phase 'ImportList:PollCommand' `
                -CommandId $command.id
            foreach ($key in $timeoutDetails.Keys) {
                $result.Details[$key] = $timeoutDetails[$key]
            }

            return $result
        }

        # Step 7: Post-sync verification
        # Check that sync actually ran for our import list by comparing state before/after.
        # Lidarr import lists don't expose lastSync on the API object, so we verify:
        # 1. Command completed without error (already done above)
        # 2. Import list still exists and has no new error state
        # 3. Log warning that this is "command completion" proof only
        $postSync = $null
        try {
            $postSync = Invoke-LidarrApi -Endpoint "importlist/$($configuredList.id)"
        }
        catch {
            $result.Errors += "Import list $($configuredList.id) not accessible after sync: $_"

            # Site C: Structured import failed details (explicit at source)
            # Include preSyncImportListFound to distinguish "disappeared" from "never existed"
            $failedDetails = New-ImportFailedDetails `
                -PluginName $PluginName `
                -ImportListId $configuredList.id `
                -Phase 'ImportList:PostSyncVerify' `
                -Endpoint "/api/v1/importlist/$($configuredList.id)" `
                -ImportListName $configuredList.name `
                -CommandId $command.id `
                -CommandStatus 'completed' `
                -PostSyncVerified $false `
                -PreSyncImportListFound $preSyncImportListFound
            foreach ($key in $failedDetails.Keys) {
                $result.Details[$key] = $failedDetails[$key]
            }

            return $result
        }

        # Check for error fields if present (Lidarr v1 API may expose these)
        $postSyncError = $null
        if ($postSync.PSObject.Properties['lastSyncError']) {
            $postSyncError = $postSync.lastSyncError
        }
        if ($postSyncError -and $postSyncError -ne '') {
            $result.PostSyncError = $postSyncError
            $result.Errors += "Import list sync error reported: $postSyncError"

            # Site C: Structured import failed details (explicit at source)
            $failedDetails = New-ImportFailedDetails `
                -PluginName $PluginName `
                -ImportListId $configuredList.id `
                -Phase 'ImportList:PostSyncVerify' `
                -Endpoint "/api/v1/importlist/$($configuredList.id)" `
                -ImportListName $configuredList.name `
                -CommandId $command.id `
                -CommandStatus 'completed' `
                -LastSyncError $postSyncError `
                -PostSyncVerified $true `
                -PreSyncImportListFound $preSyncImportListFound
            foreach ($key in $failedDetails.Keys) {
                $result.Details[$key] = $failedDetails[$key]
            }

            return $result
        }

        # Success: command completed and no error state on import list
        # Note: This proves command ran, not that items were imported (Lidarr API limitation)
        Write-Host "       Post-sync check: import list accessible, no error state" -ForegroundColor Gray
        $result.PostSyncVerified = $true
        $result.Success = $true
        $result.Outcome = 'success'
    }
    catch {
        $errMsg = "$_"
        # Check if exception is auth-related
        if ($errMsg -match '(?i)(not authenticated|oauth|authorize|token|credential|login|password|api.?key|forbidden|unauthorized|401|403|invalid_grant|invalid_client)') {
            $result.Outcome = 'skipped'
            $result.SkipReason = "ImportList gate failed with auth error: $errMsg"
            return $result
        }
        $result.Errors += "ImportList gate failed: $errMsg"
    }

    return $result
}

function Test-MetadataGate {
    <#
    .SYNOPSIS
        Gate: Verify downloaded audio files have valid metadata tags.

    .DESCRIPTION
        Optional validation that checks first N audio files for required metadata:
        - artist (non-empty)
        - album (non-empty)
        - title (non-empty)
        - disc/track numbers if multi-disc album

        Uses python3 + mutagen inside the container for tag reading.
        SKIPS (not fails) if mutagen is not available.

    .PARAMETER OutputPath
        Container path to the downloaded files directory.

    .PARAMETER ContainerName
        Docker container name where files are located.

    .PARAMETER MaxFilesToCheck
        Maximum number of files to validate (default: 3).

    .PARAMETER IsMultiDisc
        If true, also validates disc/track metadata.

    .OUTPUTS
        PSCustomObject with Success, Outcome, ValidatedFiles, Errors, SkipReason
    #>
    param(
        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [string]$ContainerName,

        [int]$MaxFilesToCheck = 3,

        [switch]$IsMultiDisc
    )

    $result = [PSCustomObject]@{
        Gate = 'Metadata'
        OutputPath = $OutputPath
        ContainerName = $ContainerName
        Outcome = 'failed'
        Success = $false
        ValidatedFiles = @()
        TotalFilesChecked = 0
        FilesWithTags = 0
        MissingTags = @()
        SampleFile = $null
        Errors = @()
        SkipReason = $null
        Details = @{}
        TagReadTool = 'unknown'
        TagReadToolVersion = $null
    }

    $currentCandidateFile = $null  # Track current file being processed for exception safety (function-level)

    try {
        # Docker prereq: explicit E2E_DOCKER_UNAVAILABLE at source (no classifier inference)
        if (Get-Command Test-E2EDockerAvailable -ErrorAction SilentlyContinue) {
            $dockerCheck = Test-E2EDockerAvailable -Phase 'Metadata:Prereq' -Operation 'docker exec'
            if (-not $dockerCheck.Success) {
                $result.Details.ErrorCode = 'E2E_DOCKER_UNAVAILABLE'
                foreach ($key in $dockerCheck.Details.Keys) { $result.Details[$key] = $dockerCheck.Details[$key] }
                $result.Errors += "E2E_DOCKER_UNAVAILABLE: $($dockerCheck.Details.Suggestion)"
                return $result
            }
        } elseif (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            $result.Details.ErrorCode = 'E2E_DOCKER_UNAVAILABLE'
            $result.Errors += 'E2E_DOCKER_UNAVAILABLE: docker CLI not found'
            return $result
        }

        # Check if container exists and is accessible
        if (Get-Command Invoke-E2EDocker -ErrorAction SilentlyContinue) {
            $inspect = Invoke-E2EDocker -Args @('inspect', $ContainerName) -TimeoutSec 5
            if (-not $inspect.Success) {
                $details = New-DockerUnavailableDetails -Phase 'Metadata:InspectContainer' -Operation 'docker inspect' -ContainerName $ContainerName -DockerExitCode $inspect.ExitCode -DockerStderr $inspect.StdErr -TimedOut:$inspect.TimedOut
                $result.Details.ErrorCode = 'E2E_DOCKER_UNAVAILABLE'
                foreach ($key in $details.Keys) { $result.Details[$key] = $details[$key] }
                $result.Errors += "E2E_DOCKER_UNAVAILABLE: $($details.Suggestion)"
                return $result
            }
        } else {
            docker inspect $ContainerName 1>$null 2>$null
            if ($LASTEXITCODE -ne 0) {
                $result.Details.ErrorCode = 'E2E_DOCKER_UNAVAILABLE'
                $result.Errors += "E2E_DOCKER_UNAVAILABLE: Container '$ContainerName' not found or not accessible"
                return $result
            }
        }

        # Check if python3 + mutagen are available in the container      
        $mutagenCheck = docker exec $ContainerName python3 -c "import mutagen; print('ok')" 2>$null
        $mutagenOk = (@($mutagenCheck) -join '').Trim() -eq 'ok'

        if (-not $mutagenOk) {
            # Host-side fallback for local development (uses TagLibSharp via dotnet).
            $result.TagReadTool = 'taglib'
            $dotnetAvailable = (Get-Command dotnet -ErrorAction SilentlyContinue) -ne $null
            $probeProjectPath = Join-Path $PSScriptRoot "..\tools\MetadataProbe\MetadataProbe.csproj"
            $probeProjectPath = [System.IO.Path]::GetFullPath($probeProjectPath)

            if (-not $dotnetAvailable -or -not (Test-Path $probeProjectPath)) {
                $result.Outcome = 'skipped'
                $result.SkipReason = "python3 + mutagen not available in container. Host fallback requires dotnet + scripts/tools/MetadataProbe (install mutagen with: python3 -m pip install mutagen)."
                return $result
            }

            Write-Host "       Mutagen not found in container; using host-side TagLibSharp fallback..." -ForegroundColor Yellow

            $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("lidarr-plugin-metadata-" + [Guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

            try {
                # Get audio files from output path (filter common audio extensions)
                $findCmd = "find '$OutputPath' -type f \( -name '*.flac' -o -name '*.m4a' -o -name '*.mp3' -o -name '*.ogg' -o -name '*.wav' \) 2>/dev/null | LC_ALL=C sort | head -n $MaxFilesToCheck"
                $audioFilesRaw = docker exec $ContainerName sh -c $findCmd 2>$null
                $audioFiles = @($audioFilesRaw) -split "`n" | Where-Object { $_.Trim() }

                if ($audioFiles.Count -eq 0) {
                    $result.Details.ErrorCode = 'E2E_ZERO_AUDIO_FILES'
                    $result.Details.ValidationPhase = 'metadata:containerScan'
                    $result.Errors += "E2E_ZERO_AUDIO_FILES: No audio files found in output path: $OutputPath"
                    return $result
                }

                $result.TotalFilesChecked = $audioFiles.Count
                Write-Host "       Copying $($audioFiles.Count) files from container for metadata validation..." -ForegroundColor Gray

                $localFiles = @()
                $i = 0
                foreach ($file in $audioFiles) {
                    $i++
                    $fileName = Split-Path -Leaf $file
                    $localPath = Join-Path $tempRoot ("{0:D2}-{1}" -f $i, $fileName)
                    docker cp "$ContainerName`:$file" "$localPath" 2>$null | Out-Null
                    if (Test-Path $localPath) {
                        $localFiles += $localPath
                    }
                    else {
                        $result.Errors += "Failed to copy file from container: $file"
                    }
                }

                if ($localFiles.Count -eq 0) {
                    $result.Details.ErrorCode = 'E2E_ZERO_AUDIO_FILES'
                    $result.Details.ValidationPhase = 'metadata:copiedFilesScan'
                    $result.Errors += "E2E_ZERO_AUDIO_FILES: No files could be copied from container for metadata validation."
                    return $result
                }

                $probeJson = dotnet run -c Release --project $probeProjectPath -- @($localFiles) 2>$null
                $probeJsonText = (@($probeJson) -join '').Trim()
                if (-not $probeJsonText) {
                    $result.Errors += "Host metadata probe returned no output."
                    return $result
                }

                $probeResults = $probeJsonText | ConvertFrom-Json
                $validatedFiles = @()
                $filesWithTags = 0
                $missingTags = @()

                foreach ($probe in $probeResults) {
                    $fileName = [string]$probe.Name
                    $currentCandidateFile = $fileName  # Set BEFORE any checks for exception safety

                    if ($probe.Error) {
                        $missingTags += "${fileName}: Error reading tags: $($probe.Error)"
                        if (-not $result.SampleFile) { $result.SampleFile = $fileName }
                        continue
                    }

                    $missing = @()
                    if ([string]::IsNullOrWhiteSpace($probe.Artist)) { $missing += "artist" }
                    if ([string]::IsNullOrWhiteSpace($probe.Album)) { $missing += "album" }
                    if ([string]::IsNullOrWhiteSpace($probe.Title)) { $missing += "title" }
                    if ($null -eq $probe.Track -or $probe.Track -lt 1) { $missing += "track" }
                    if ($IsMultiDisc -and ($null -eq $probe.Disc -or $probe.Disc -lt 1)) { $missing += "disc" }

                    if ($missing.Count -gt 0) {
                        $missingTags += "${fileName}: Missing tags: $($missing -join ', ')"
                        if (-not $result.SampleFile) { $result.SampleFile = $fileName }
                        continue
                    }

                    $filesWithTags++
                    $validatedFiles += [PSCustomObject]@{
                        Name = $fileName
                        Artist = $probe.Artist
                        Album = $probe.Album
                        Title = $probe.Title
                        Track = $probe.Track
                        Disc = $probe.Disc
                    }
                    Write-Host "         OK: artist='$($probe.Artist)', album='$($probe.Album)', title='$($probe.Title)', track=$($probe.Track)" -ForegroundColor Green
                }

                $result.ValidatedFiles = $validatedFiles
                $result.FilesWithTags = $filesWithTags
                $result.MissingTags = $missingTags

                if ($missingTags.Count -eq 0 -and $filesWithTags -gt 0) {
                    $result.Success = $true
                    $result.Outcome = 'success'
                    Write-Host "       PASS: All $filesWithTags files have valid metadata tags" -ForegroundColor Green
                }
                elseif ($filesWithTags -gt 0 -and $missingTags.Count -gt 0) {
                    # Partial success - some files ok, some missing
                    $result.Details.ErrorCode = 'E2E_METADATA_MISSING'
                    $result.Details.tagReadTool = $result.TagReadTool
                    $result.Details.tagReadToolVersion = $result.TagReadToolVersion
                    $result.Details.audioFilesValidated = $result.TotalFilesChecked
                    $result.Details.audioFilesWithMissingTags = $missingTags.Count
                    $result.Details.sampleFile = $result.SampleFile
                    # Cap missingTags at 10 entries
                    $cappedTags = @($missingTags | Select-Object -First 10)
                    $result.Details.missingTags = $cappedTags
                    $result.Details.missingTagsCount = $missingTags.Count
                    $result.Details.missingTagsCapped = ($missingTags.Count -gt 10)
                    $result.Errors += "Metadata validation failed for $($missingTags.Count) of $($result.TotalFilesChecked) files"
                    foreach ($m in $missingTags) {
                        $result.Errors += "  FAIL: $m"
                        Write-Host "       FAIL: $m" -ForegroundColor Red
                    }
                }
                else {
                    # All files failed
                    $result.Details.ErrorCode = 'E2E_METADATA_MISSING'
                    $result.Details.tagReadTool = $result.TagReadTool
                    $result.Details.tagReadToolVersion = $result.TagReadToolVersion
                    $result.Details.audioFilesValidated = $result.TotalFilesChecked
                    $result.Details.audioFilesWithMissingTags = $missingTags.Count
                    $result.Details.sampleFile = $result.SampleFile
                    # Cap missingTags at 10 entries
                    $cappedTags = @($missingTags | Select-Object -First 10)
                    $result.Details.missingTags = $cappedTags
                    $result.Details.missingTagsCount = $missingTags.Count
                    $result.Details.missingTagsCapped = ($missingTags.Count -gt 10)
                    $result.Errors += "No files passed metadata validation"
                    foreach ($m in $missingTags) {
                        $result.Errors += "  FAIL: $m"
                        Write-Host "       FAIL: $m" -ForegroundColor Red
                    }
                }

                return $result
            }
            finally {
                try { Remove-Item -Recurse -Force -Path $tempRoot -ErrorAction SilentlyContinue } catch { }
            }
        }

        # Using mutagen (container python path)
        $result.TagReadTool = 'mutagen'

        # Get audio files from output path (filter common audio extensions)
        $findCmd = "find '$OutputPath' -type f \( -name '*.flac' -o -name '*.m4a' -o -name '*.mp3' -o -name '*.ogg' -o -name '*.wav' \) 2>/dev/null | LC_ALL=C sort | head -n $MaxFilesToCheck"
        $audioFilesRaw = docker exec $ContainerName sh -c $findCmd 2>$null
        $audioFiles = @($audioFilesRaw) -split "`n" | Where-Object { $_.Trim() }

        if ($audioFiles.Count -eq 0) {
            $result.Details.ErrorCode = 'E2E_ZERO_AUDIO_FILES'
            $result.Details.ValidationPhase = 'metadata:containerScan'
            $result.Errors += "E2E_ZERO_AUDIO_FILES: No audio files found in output path: $OutputPath"
            return $result
        }

        $result.TotalFilesChecked = $audioFiles.Count
        Write-Host "       Checking metadata for $($audioFiles.Count) files..." -ForegroundColor Gray

        # Python script to read metadata using mutagen
        # Returns JSON: {"artist": "...", "album": "...", "title": "...", "track": N, "disc": N, "error": "..."}
        $pythonScript = @'
import sys
import json
import mutagen
from mutagen.flac import FLAC
from mutagen.mp4 import MP4
from mutagen.mp3 import MP3
from mutagen.oggvorbis import OggVorbis

def get_tags(filepath):
    result = {"artist": "", "album": "", "title": "", "track": None, "disc": None, "error": None}
    try:
        audio = mutagen.File(filepath, easy=True)
        if audio is None:
            result["error"] = "Cannot read file"
            return result

        # Easy tags use list values
        def get_tag(keys):
            for k in keys:
                if k in audio:
                    v = audio[k]
                    if isinstance(v, list) and len(v) > 0:
                        return str(v[0])
                    elif v:
                        return str(v)
            return ""

        result["artist"] = get_tag(["artist", "albumartist", "performer"])
        result["album"] = get_tag(["album"])
        result["title"] = get_tag(["title"])

        # Track number (may be "N/M" format)
        track_str = get_tag(["tracknumber", "track"])
        if track_str:
            try:
                result["track"] = int(track_str.split("/")[0])
            except:
                pass

        # Disc number (may be "N/M" format) - includes "disk" for MP4/M4A compatibility
        disc_str = get_tag(["discnumber", "disc", "disk"])
        if disc_str:
            try:
                result["disc"] = int(disc_str.split("/")[0])
            except:
                pass

    except Exception as e:
        result["error"] = str(e)
    return result

if __name__ == "__main__":
    print(json.dumps(get_tags(sys.argv[1])))
'@

        # Escape for shell
        $pythonScriptEscaped = $pythonScript -replace "'", "'\''"

        $validatedFiles = @()
        $filesWithTags = 0
        $missingTags = @()

        foreach ($file in $audioFiles) {
            $fileName = Split-Path -Leaf $file
            $currentCandidateFile = $fileName  # Set BEFORE any IO for exception safety
            Write-Host "       Checking: $fileName" -ForegroundColor DarkGray

            # Run python script in container
            $jsonResult = docker exec $ContainerName python3 -c $pythonScriptEscaped "$file" 2>$null
            $jsonStr = (@($jsonResult) -join '').Trim()

            if (-not $jsonStr) {
                $missingTags += "${fileName}: Failed to read tags (no output)"
                if (-not $result.SampleFile) { $result.SampleFile = $fileName }
                continue
            }

            try {
                $tags = $jsonStr | ConvertFrom-Json
            }
            catch {
                $missingTags += "${fileName}: Failed to parse tag JSON: $jsonStr"
                if (-not $result.SampleFile) { $result.SampleFile = $fileName }
                continue
            }

            if ($tags.error) {
                $missingTags += "${fileName}: Error reading tags: $($tags.error)"
                if (-not $result.SampleFile) { $result.SampleFile = $fileName }
                continue
            }

            # Validate required tags
            $missing = @()
            if ([string]::IsNullOrWhiteSpace($tags.artist)) { $missing += "artist" }
            if ([string]::IsNullOrWhiteSpace($tags.album)) { $missing += "album" }
            if ([string]::IsNullOrWhiteSpace($tags.title)) { $missing += "title" }

            # Track number is expected for all files
            if ($null -eq $tags.track -or $tags.track -lt 1) { $missing += "track" }

            # Disc number only required for multi-disc
            if ($IsMultiDisc -and ($null -eq $tags.disc -or $tags.disc -lt 1)) {
                $missing += "disc"
            }

            if ($missing.Count -gt 0) {
                $missingTags += "${fileName}: Missing tags: $($missing -join ', ')"
                if (-not $result.SampleFile) { $result.SampleFile = $fileName }
            }
            else {
                $filesWithTags++
                $validatedFiles += [PSCustomObject]@{
                    Path = $file
                    Name = $fileName
                    Artist = $tags.artist
                    Album = $tags.album
                    Title = $tags.title
                    Track = $tags.track
                    Disc = $tags.disc
                }
                Write-Host "         OK: artist='$($tags.artist)', album='$($tags.album)', title='$($tags.title)', track=$($tags.track)" -ForegroundColor Green
            }
        }

        $result.ValidatedFiles = $validatedFiles
        $result.FilesWithTags = $filesWithTags
        $result.MissingTags = $missingTags

        # Success if all checked files have valid tags
        if ($missingTags.Count -eq 0 -and $filesWithTags -gt 0) {
            $result.Success = $true
            $result.Outcome = 'success'
            Write-Host "       PASS: All $filesWithTags files have valid metadata tags" -ForegroundColor Green
        }
        elseif ($filesWithTags -gt 0 -and $missingTags.Count -gt 0) {
            # Partial success - some files ok, some missing
            $result.Details.ErrorCode = 'E2E_METADATA_MISSING'
            $result.Details.tagReadTool = $result.TagReadTool
            $result.Details.tagReadToolVersion = $result.TagReadToolVersion
            $result.Details.audioFilesValidated = $result.TotalFilesChecked
            $result.Details.audioFilesWithMissingTags = $missingTags.Count
            $result.Details.sampleFile = $result.SampleFile
            # Cap missingTags at 10 entries
            $cappedTags = @($missingTags | Select-Object -First 10)
            $result.Details.missingTags = $cappedTags
            $result.Details.missingTagsCount = $missingTags.Count
            $result.Details.missingTagsCapped = ($missingTags.Count -gt 10)
            $result.Errors += "Metadata validation failed for $($missingTags.Count) of $($audioFiles.Count) files"
            foreach ($m in $missingTags) {
                $result.Errors += "  FAIL: $m"
                Write-Host "       FAIL: $m" -ForegroundColor Red
            }
        }
        else {
            # All files failed
            $result.Details.ErrorCode = 'E2E_METADATA_MISSING'
            $result.Details.tagReadTool = $result.TagReadTool
            $result.Details.tagReadToolVersion = $result.TagReadToolVersion
            $result.Details.audioFilesValidated = $result.TotalFilesChecked
            $result.Details.audioFilesWithMissingTags = $missingTags.Count
            $result.Details.sampleFile = $result.SampleFile
            # Cap missingTags at 10 entries
            $cappedTags = @($missingTags | Select-Object -First 10)
            $result.Details.missingTags = $cappedTags
            $result.Details.missingTagsCount = $missingTags.Count
            $result.Details.missingTagsCapped = ($missingTags.Count -gt 10)
            $result.Errors += "No files passed metadata validation"
            foreach ($m in $missingTags) {
                $result.Errors += "  FAIL: $m"
                Write-Host "       FAIL: $m" -ForegroundColor Red
            }
        }
    }
    catch {
        $result.Errors += "Metadata gate failed: $_"
        # Ensure SampleFile is set even if exception occurred during processing
        if (-not $result.SampleFile -and $currentCandidateFile) {
            $result.SampleFile = $currentCandidateFile
        }
    }

    return $result
}

function Test-AudioFileValidation {
    <#
    .SYNOPSIS
        Validates downloaded audio files have non-zero size and valid magic bytes.

    .DESCRIPTION
        Checks up to N audio files in a container path for:
        1. Non-zero file size
        2. Valid audio magic bytes (FLAC: fLaC, MP3: ID3/0xFFxFB, M4A: ftyp, OGG: OggS)

        Returns file validation details for manifest output.

    .PARAMETER OutputPath
        Container path to the downloaded files directory.

    .PARAMETER ContainerName
        Docker container name where files are located.

    .PARAMETER MaxFilesToCheck
        Maximum number of files to validate (default: 3).

    .OUTPUTS
        PSCustomObject with Success, ValidatedFiles, Errors, etc.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [string]$ContainerName,

        [int]$MaxFilesToCheck = 3
    )

    $result = [PSCustomObject]@{
        Success = $false
        TotalFilesFound = 0
        ValidatedFiles = @()
        FailedFiles = @()
        Errors = @()
        ErrorCode = $null
        ValidationPhase = $null
    }

    try {
        # Docker prereq: explicit E2E_DOCKER_UNAVAILABLE at source (no classifier inference)
        if (Get-Command Test-E2EDockerAvailable -ErrorAction SilentlyContinue) {
            $dockerCheck = Test-E2EDockerAvailable -Phase 'Grab:FileValidation' -Operation 'docker exec'
            if (-not $dockerCheck.Success) {
                $result.ErrorCode = 'E2E_DOCKER_UNAVAILABLE'
                $result.ValidationPhase = 'grab:fileValidation'
                $result.Errors += "E2E_DOCKER_UNAVAILABLE: $($dockerCheck.Details.Suggestion)"
                return $result
            }
        } elseif (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            $result.ErrorCode = 'E2E_DOCKER_UNAVAILABLE'
            $result.ValidationPhase = 'grab:fileValidation'
            $result.Errors += 'E2E_DOCKER_UNAVAILABLE: docker CLI not found'
            return $result
        }

        # Find audio files in output path
        $findCmd = "find '$OutputPath' -type f \( -name '*.flac' -o -name '*.m4a' -o -name '*.mp3' -o -name '*.ogg' -o -name '*.wav' \) 2>/dev/null | LC_ALL=C sort | head -n $MaxFilesToCheck"
        $audioFilesRaw = docker exec $ContainerName sh -c $findCmd 2>$null
        $audioFiles = @($audioFilesRaw) -split "`n" | Where-Object { $_.Trim() }

        if ($audioFiles.Count -eq 0) {
            $result.ErrorCode = 'E2E_ZERO_AUDIO_FILES'
            $result.ValidationPhase = 'grab:fileValidation'
            $result.Errors += "E2E_ZERO_AUDIO_FILES: No audio files found in: $OutputPath"
            return $result
        }

        $result.TotalFilesFound = $audioFiles.Count

        # Audio magic byte signatures
        # FLAC: 66 4C 61 43 (fLaC)
        # MP3 ID3: 49 44 33 (ID3)
        # MP3 sync: FF FB or FF FA or FF F3 or FF F2
        # M4A/MP4: 'ftyp' at offset 4 (xx xx xx xx 66 74 79 70)
        # OGG: 4F 67 67 53 (OggS)
        # WAV: 52 49 46 46 (RIFF)

        foreach ($filePath in $audioFiles) {
            $fileName = Split-Path $filePath -Leaf
            $fileValid = $false
            $failReason = $null

            # Get file size
            $sizeCmd = "stat -c %s '$filePath' 2>/dev/null || stat -f %z '$filePath' 2>/dev/null"
            $sizeRaw = docker exec $ContainerName sh -c $sizeCmd 2>$null
            $fileSize = [int64]0
            [int64]::TryParse($sizeRaw.Trim(), [ref]$fileSize) | Out-Null

            if ($fileSize -le 0) {
                $failReason = "Zero or invalid file size"
                $result.FailedFiles += [PSCustomObject]@{
                    Name = $fileName
                    Path = $filePath
                    Size = $fileSize
                    Reason = $failReason
                }
                continue
            }

            # Get first 12 bytes as hex for magic byte check
            $hexCmd = "od -A n -t x1 -N 12 '$filePath' 2>/dev/null | tr -d ' \n'"
            $magicHex = docker exec $ContainerName sh -c $hexCmd 2>$null
            $magicHex = $magicHex.Trim().ToLower()

            # Check magic bytes
            $ext = [System.IO.Path]::GetExtension($filePath).ToLower()
            switch ($ext) {
                '.flac' {
                    # FLAC: starts with 'fLaC' (66 4c 61 43)
                    if ($magicHex.StartsWith('664c6143')) {
                        $fileValid = $true
                    } else {
                        $failReason = "Invalid FLAC magic bytes (expected 664c6143, got $($magicHex.Substring(0,8)))"
                    }
                }
                '.mp3' {
                    # MP3: ID3 tag (49 44 33) or sync word (ff fb, ff fa, ff f3, ff f2)
                    if ($magicHex.StartsWith('494433') -or
                        $magicHex.StartsWith('fffb') -or
                        $magicHex.StartsWith('fffa') -or
                        $magicHex.StartsWith('fff3') -or
                        $magicHex.StartsWith('fff2')) {
                        $fileValid = $true
                    } else {
                        $failReason = "Invalid MP3 magic bytes (expected ID3/sync, got $($magicHex.Substring(0,6)))"
                    }
                }
                '.m4a' {
                    # M4A: 'ftyp' at offset 4 (xx xx xx xx 66 74 79 70)
                    if ($magicHex.Length -ge 16 -and $magicHex.Substring(8,8) -eq '66747970') {
                        $fileValid = $true
                    } else {
                        $failReason = "Invalid M4A magic bytes (expected ftyp at offset 4)"
                    }
                }
                '.ogg' {
                    # OGG: starts with 'OggS' (4f 67 67 53)
                    if ($magicHex.StartsWith('4f676753')) {
                        $fileValid = $true
                    } else {
                        $failReason = "Invalid OGG magic bytes (expected 4f676753, got $($magicHex.Substring(0,8)))"
                    }
                }
                '.wav' {
                    # WAV: starts with 'RIFF' (52 49 46 46)
                    if ($magicHex.StartsWith('52494646')) {
                        $fileValid = $true
                    } else {
                        $failReason = "Invalid WAV magic bytes (expected 52494646, got $($magicHex.Substring(0,8)))"
                    }
                }
                default {
                    # Unknown extension - just check non-zero size
                    $fileValid = $true
                }
            }

            if ($fileValid) {
                $result.ValidatedFiles += [PSCustomObject]@{
                    Name = $fileName
                    Path = $filePath
                    Size = $fileSize
                    Extension = $ext
                    MagicValid = $true
                }
            } else {
                $result.FailedFiles += [PSCustomObject]@{
                    Name = $fileName
                    Path = $filePath
                    Size = $fileSize
                    Reason = $failReason
                    MagicHex = $magicHex
                }
            }
        }

        if ($result.ValidatedFiles.Count -gt 0 -and $result.FailedFiles.Count -eq 0) {
            $result.Success = $true
        } elseif ($result.FailedFiles.Count -gt 0) {
            foreach ($f in $result.FailedFiles) {
                $result.Errors += "$($f.Name): $($f.Reason)"
            }
        }
    }
    catch {
        $result.Errors += "File validation failed: $_"
    }

    return $result
}

function Test-LLMEndpoint {
    <#
    .SYNOPSIS
        Probes an LLM endpoint to detect type (OpenAI/LM Studio vs Ollama) and verify connectivity.

    .DESCRIPTION
        Attempts to detect the LLM API type by probing known endpoints:
        - OpenAI/LM Studio: GET /v1/models
        - Ollama: GET /api/tags

        Returns endpoint info including kind, model count, and first model ID.

    .PARAMETER BaseUrl
        The base URL of the LLM endpoint (e.g., http://localhost:1234)

    .PARAMETER TimeoutSec
        Request timeout in seconds (default: 10)

    .OUTPUTS
        PSCustomObject with: Success, Kind, ModelsCount, FirstModelId, Error
    #>
    param(
        [Parameter(Mandatory)]
        [string]$BaseUrl,

        [int]$TimeoutSec = 10
    )

    $result = [PSCustomObject]@{
        Success = $false
        Kind = $null
        ModelsCount = 0
        FirstModelId = $null
        Models = @()
        Error = $null
    }

    $BaseUrl = $BaseUrl.TrimEnd('/')

    # Helper to validate JSON response (catches HTML from reverse proxies)
    function Test-JsonResponse {
        param([Microsoft.PowerShell.Commands.WebResponseObject]$Response)

        # Check Content-Type header
        $contentType = $Response.Headers['Content-Type']
        if ($contentType) {
            $ct = if ($contentType -is [array]) { $contentType[0] } else { "$contentType" }
            if ($ct -notmatch 'application/json' -and $ct -notmatch 'text/json') {
                return $null  # Not JSON
            }
        }

        # Try parsing as JSON
        try {
            $json = $Response.Content | ConvertFrom-Json
            return $json
        }
        catch {
            return $null  # Invalid JSON
        }
    }

    # Try OpenAI-compatible endpoint first (LM Studio, OpenAI, vLLM, etc.)
    try {
        $openaiUrl = "$BaseUrl/v1/models"
        $webResponse = Invoke-WebRequest -Uri $openaiUrl -Method GET -TimeoutSec $TimeoutSec -ErrorAction Stop
        $response = Test-JsonResponse -Response $webResponse

        if ($response -and ($response.data -or $response.object -eq 'list')) {
            $models = @()
            if ($response.data) {
                $models = @($response.data)
            }

            $result.Success = $true
            $result.Kind = 'openai-compatible'
            $result.ModelsCount = $models.Count
            $result.Models = $models | ForEach-Object { $_.id }

            if ($models.Count -gt 0) {
                $result.FirstModelId = $models[0].id
                # Detect LM Studio specifically by model ID pattern or owned_by
                if ($models[0].owned_by -eq 'lmstudio' -or $models[0].id -match 'lmstudio') {
                    $result.Kind = 'lmstudio'
                }
            }

            return $result
        }
    }
    catch {
        # OpenAI endpoint failed, try Ollama
    }

    # Try Ollama endpoint
    try {
        $ollamaUrl = "$BaseUrl/api/tags"
        $webResponse = Invoke-WebRequest -Uri $ollamaUrl -Method GET -TimeoutSec $TimeoutSec -ErrorAction Stop
        $response = Test-JsonResponse -Response $webResponse

        if ($response -and ($response.models -or $response -is [array])) {
            $models = @()
            if ($response.models) {
                $models = @($response.models)
            } elseif ($response -is [array]) {
                $models = @($response)
            }

            $result.Success = $true
            $result.Kind = 'ollama'
            $result.ModelsCount = $models.Count
            $result.Models = $models | ForEach-Object { $_.name ?? $_.model ?? $_ }

            if ($models.Count -gt 0) {
                $result.FirstModelId = $models[0].name ?? $models[0].model ?? "$($models[0])"
            }

            return $result
        }
    }
    catch {
        # Ollama endpoint also failed
    }

    # Both failed - try a simple connectivity check
    try {
        $null = Invoke-WebRequest -Uri $BaseUrl -Method GET -TimeoutSec $TimeoutSec -ErrorAction Stop
        $result.Error = "Endpoint reachable but not OpenAI or Ollama compatible"
    }
    catch {
        $result.Error = "Endpoint unreachable: $_"
    }

    return $result
}

function Test-LLMModelAvailability {
    <#
    .SYNOPSIS
        Checks if an expected model ID exists in a probed model list.

    .DESCRIPTION
        Performs an exact (case-insensitive) match. No substring/wildcard matching.

        If ExpectedModelId is null/empty/whitespace, returns $true (no constraint).
    #>
    [CmdletBinding()]
    param(
        [string[]]$Models,

        [string]$ExpectedModelId
    )

    if ([string]::IsNullOrWhiteSpace("$ExpectedModelId")) {
        return $true
    }

    $expected = "$ExpectedModelId".Trim()
    foreach ($model in @($Models)) {
        if ([string]::Equals("$model", $expected, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-BrainarrLLMGate {
    <#
    .SYNOPSIS
        Gate: Brainarr LLM functional test - proof-of-life and sync verification.

    .DESCRIPTION
        Opt-in gate that validates Brainarr LLM integration:
        1. Probes LLM endpoint (OpenAI/LM Studio or Ollama) for proof-of-life
        2. Creates/updates Brainarr import list with LLM configuration
        3. Triggers ImportListSync command
        4. Verifies sync completed without errors

        SKIPs (not FAILs) if LLM endpoint unreachable, unless StrictMode is enabled.

    .PARAMETER LlmBaseUrl
        Base URL of the LLM endpoint (e.g., http://localhost:1234)

    .PARAMETER ModelId
        Specific model ID to configure (optional - uses auto-detect if not specified)

    .PARAMETER ExpectedModelId
        If specified, the gate FAILs unless the endpoint reports an exact (case-insensitive) match.

    .PARAMETER StrictMode
        When true, FAIL instead of SKIP if LLM endpoint is unreachable   

    .PARAMETER CommandTimeoutSec
        Timeout for ImportListSync command (default: 120)

    .OUTPUTS
        PSCustomObject with gate result including LLM details
    #>
    param(
        [Parameter(Mandatory)]
        [string]$LlmBaseUrl,

        [string]$ModelId = $null,

        [string]$ExpectedModelId = $null,

        [switch]$StrictMode,

        [int]$CommandTimeoutSec = 120
    )

    $result = [PSCustomObject]@{
        Gate = 'BrainarrLLM'
        PluginName = 'Brainarr'
        Outcome = 'failed'
        Success = $false
        SkipReason = $null
        Errors = @()
        Details = [ordered]@{
            llmKind = $null
            modelsCount = 0
            firstModelId = $null
            endpointRedacted = $null
            expectedModelFound = $null
            expectedModelIdHash = $null
            importListId = $null
            commandId = $null
            syncCompleted = $false
            lastSyncError = $null
        }
    }

    # Redact the endpoint URL for manifest
    $redactedUrl = $LlmBaseUrl -replace '(?i)(https?://)[^:/]+', '$1[REDACTED-HOST]'
    # Preserve port if present
    if ($LlmBaseUrl -match ':(\d+)') {
        $port = $Matches[1]
        $redactedUrl = $redactedUrl -replace '\[REDACTED-HOST\]', "[REDACTED-HOST]:$port"
    }
    $result.Details.endpointRedacted = $redactedUrl

    try {
        # Step 1: Probe LLM endpoint for proof-of-life
        Write-Host "       Probing LLM endpoint..." -ForegroundColor Gray
        $llmProbe = Test-LLMEndpoint -BaseUrl $LlmBaseUrl -TimeoutSec 10

        if (-not $llmProbe.Success) {
            $reason = "LLM endpoint unreachable: $($llmProbe.Error)"
            $result.Details.llmKind = 'unknown'

            # Detect if this is a timeout error
            $isTimeout = $llmProbe.Error -match '(?i)(timed?\s*out|timeout|operation.*canceled|task.*canceled)'
            if ($isTimeout) {
                # HTTP timeout for LLM endpoint
                $timeoutDetails = New-ApiTimeoutDetails `
                    -TimeoutType 'http' `
                    -TimeoutSeconds 10 `
                    -Endpoint '/v1/models' `
                    -Operation 'BrainarrLLMEndpoint' `
                    -PluginName 'Brainarr' `
                    -Phase 'BrainarrLLM:DetectModels'
                foreach ($key in $timeoutDetails.Keys) {
                    $result.Details[$key] = $timeoutDetails[$key]
                }
            }

            if ($StrictMode) {
                $result.Errors += $reason
                return $result
            }
            $result.Outcome = 'skipped'
            $result.SkipReason = $reason
            return $result
        }

        $result.Details.llmKind = $llmProbe.Kind
        $result.Details.modelsCount = $llmProbe.ModelsCount

        # Redact model ID if it looks like it contains sensitive info (long alphanumeric)
        # Also compute SHA256 prefix hash for correlation without leaking full string
        $firstModel = $llmProbe.FirstModelId
        $modelIdHash = $null
        if ($firstModel) {
            # Compute SHA256 hash prefix (first 12 chars) for correlation
            $sha256 = [System.Security.Cryptography.SHA256]::Create()
            $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($firstModel))
            $modelIdHash = [BitConverter]::ToString($hashBytes).Replace('-', '').ToLower().Substring(0, 12)

            if ($firstModel.Length -gt 50) {
                $firstModel = $firstModel.Substring(0, 30) + "...[TRUNCATED]"
            }
        }
        $result.Details.firstModelId = $firstModel
        $result.Details.modelIdHash = $modelIdHash

        $expectedModel = $ExpectedModelId
        if (-not [string]::IsNullOrWhiteSpace("$expectedModel")) {
            $expectedModel = "$expectedModel".Trim()
            $expectedHash = $null
            try {
                $sha256Expected = [System.Security.Cryptography.SHA256]::Create()
                $hashBytesExpected = $sha256Expected.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($expectedModel))
                $expectedHash = [BitConverter]::ToString($hashBytesExpected).Replace('-', '').ToLower().Substring(0, 12)
            }
            catch {
                $expectedHash = $null
            }
            $result.Details.expectedModelIdHash = $expectedHash

            $found = Test-LLMModelAvailability -Models @($llmProbe.Models) -ExpectedModelId $expectedModel
            $result.Details.expectedModelFound = $found

            # Config validation: if we have an expectedModel but failed to compute hash, that is a config issue
            if ([string]::IsNullOrWhiteSpace($expectedHash)) {
                # E2E_INTERNAL_ERROR: Hash computation failed (rare edge case)
                $result.Details.ErrorCode = 'E2E_INTERNAL_ERROR'
                $result.Details.phase = 'BrainarrLLM:ComputeExpectedModelHash'
                $result.Details.reason = 'ExpectedModelHashComputationFailed'
                $result.Details.note = 'SHA256 hash computation failed for expected model ID'
                $result.Details.llmKind = $llmProbe.Kind ?? 'unknown'
                $result.Details.modelsCount = [int]($llmProbe.ModelsCount ?? 0)
                $result.Details.expectedModelFound = $false
                $result.Errors += "Expected model ID provided but hash computation failed - verify model ID configuration."
                return $result
            }

            if (-not $found) {
                # E2E_PROVIDER_UNAVAILABLE: LLM endpoint is reachable but expected model not available
                # Set all contract fields defensively
                $result.Details.ErrorCode = 'E2E_PROVIDER_UNAVAILABLE'
                $result.Details.llmKind = $llmProbe.Kind ?? 'unknown'
                $result.Details.modelsCount = [int]($llmProbe.ModelsCount ?? 0)
                $result.Details.expectedModelIdHash = $expectedHash
                $result.Details.expectedModelFound = $false
                $result.Errors += "Expected model not found on LLM endpoint (expectedModelIdHash=$expectedHash, modelsCount=$($result.Details.modelsCount))."
                return $result
            }
        }
        else {
            $result.Details.expectedModelFound = $null
            $result.Details.expectedModelIdHash = $null
        }

        Write-Host "       LLM endpoint: $($llmProbe.Kind), $($llmProbe.ModelsCount) model(s)" -ForegroundColor Green
        if ($llmProbe.FirstModelId) {
            Write-Host "       First model: $firstModel (hash: $modelIdHash)" -ForegroundColor Gray
        }

        # Determine which model to use
        $effectiveModelId = $ModelId
        if (-not $effectiveModelId -and $llmProbe.FirstModelId) {
            $effectiveModelId = $llmProbe.FirstModelId
        }

        if (-not $effectiveModelId) {
            if ($StrictMode) {
                # E2E_PROVIDER_UNAVAILABLE: LLM endpoint reachable but no usable model
                $result.Details.ErrorCode = 'E2E_PROVIDER_UNAVAILABLE'
                $result.Details.llmKind = $llmProbe.Kind ?? 'unknown'
                $result.Details.modelsCount = [int]($llmProbe.ModelsCount ?? 0)
                $result.Details.expectedModelFound = $false
                $result.Errors += "No model available on LLM endpoint (modelsCount=$($result.Details.modelsCount))."
                return $result
            }
            $result.Outcome = 'skipped'
            $result.SkipReason = "No model available on LLM endpoint (use -ModelId to specify)"
            return $result
        }

        # Step 2: Find or create Brainarr import list
        Write-Host "       Configuring Brainarr import list..." -ForegroundColor Gray

        $importLists = Invoke-LidarrApi -Endpoint "importlist"
        $brainarrList = $importLists | Where-Object {
            $_.implementation -like '*Brainarr*' -or
            $_.name -like '*Brainarr*'
        } | Select-Object -First 1

        if (-not $brainarrList) {
            # Need to create import list - get schema first
            $schemas = Invoke-LidarrApi -Endpoint "importlist/schema"
            $brainarrSchema = $schemas | Where-Object { $_.implementation -like '*Brainarr*' } | Select-Object -First 1

            if (-not $brainarrSchema) {
                $result.Errors += "Brainarr import list schema not found - plugin may not be installed"
                return $result
            }

            # Build new import list from schema
            $newList = @{
                name = "Brainarr LLM"
                implementation = $brainarrSchema.implementation
                implementationName = $brainarrSchema.implementationName
                configContract = $brainarrSchema.configContract
                enableAutomaticAdd = $true
                shouldMonitor = 'entireArtist'
                shouldMonitorExisting = $false
                shouldSearch = $false
                rootFolderPath = "/downloads"
                monitorNewItems = 'none'
                qualityProfileId = 1
                metadataProfileId = 1
                fields = @()
            }

            # Copy fields from schema and update LLM config
            foreach ($field in $brainarrSchema.fields) {
                $fieldCopy = @{
                    name = $field.name
                    value = $field.value
                }

                # Set LLM configuration fields
                switch ($field.name) {
                    'configurationUrl' { $fieldCopy.value = $LlmBaseUrl }
                    'manualModelId' { $fieldCopy.value = $effectiveModelId }
                    'autoDetectModel' { $fieldCopy.value = [string]::IsNullOrEmpty($ModelId) }
                    'provider' {
                        $fieldCopy.value = switch ($llmProbe.Kind) {
                            'ollama' { 'Ollama' }
                            'lmstudio' { 'LMStudio' }
                            default { 'OpenAI' }
                        }
                    }
                }

                $newList.fields += $fieldCopy
            }

            $brainarrList = Invoke-LidarrApi -Endpoint "importlist" -Method POST -Body $newList
            Write-Host "       Created Brainarr import list (id=$($brainarrList.id))" -ForegroundColor Green
        }
        else {
            # Update existing import list with LLM config
            $updateBody = @{
                id = $brainarrList.id
                name = $brainarrList.name
                implementation = $brainarrList.implementation
                implementationName = $brainarrList.implementationName
                configContract = $brainarrList.configContract
                enableAutomaticAdd = $brainarrList.enableAutomaticAdd
                shouldMonitor = $brainarrList.shouldMonitor
                shouldMonitorExisting = $brainarrList.shouldMonitorExisting
                shouldSearch = $brainarrList.shouldSearch
                rootFolderPath = $brainarrList.rootFolderPath
                monitorNewItems = $brainarrList.monitorNewItems
                qualityProfileId = $brainarrList.qualityProfileId
                metadataProfileId = $brainarrList.metadataProfileId
                fields = @()
            }

            foreach ($field in $brainarrList.fields) {
                $fieldCopy = @{
                    name = $field.name
                    value = $field.value
                }

                # Only update LLM-related fields, preserve user tuning
                switch ($field.name) {
                    'configurationUrl' { $fieldCopy.value = $LlmBaseUrl }
                    'manualModelId' { $fieldCopy.value = $effectiveModelId }
                    'autoDetectModel' { $fieldCopy.value = [string]::IsNullOrEmpty($ModelId) }
                    'provider' {
                        $fieldCopy.value = switch ($llmProbe.Kind) {
                            'ollama' { 'Ollama' }
                            'lmstudio' { 'LMStudio' }
                            default { 'OpenAI' }
                        }
                    }
                }

                $updateBody.fields += $fieldCopy
            }

            $brainarrList = Invoke-LidarrApi -Endpoint "importlist/$($brainarrList.id)" -Method PUT -Body $updateBody
            Write-Host "       Updated Brainarr import list (id=$($brainarrList.id))" -ForegroundColor Green
        }

        $result.Details.importListId = $brainarrList.id

        # Step 3: Trigger ImportListSync command
        Write-Host "       Triggering ImportListSync..." -ForegroundColor Gray

        $command = Invoke-LidarrApi -Endpoint "command" -Method POST -Body @{
            name = "ImportListSync"
            importListId = $brainarrList.id
        }

        if (-not $command -or -not $command.id) {
            $result.Errors += "Failed to trigger ImportListSync command"
            return $result
        }

        $result.Details.commandId = $command.id
        Write-Host "       Command started (id=$($command.id))" -ForegroundColor Gray

        # Step 4: Wait for command completion
        $deadline = (Get-Date).AddSeconds($CommandTimeoutSec)
        $completed = $false
        $commandStatus = $null

        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            $commandStatus = Invoke-LidarrApi -Endpoint "command/$($command.id)"

            if ($commandStatus.status -eq 'completed') {
                $completed = $true
                Write-Host "       Command completed" -ForegroundColor Green
                break
            }
            elseif ($commandStatus.status -eq 'failed') {
                $failMsg = $commandStatus.message ?? "Unknown error"
                $result.Errors += "ImportListSync command failed: $failMsg"
                return $result
            }
            else {
                Write-Host "       Status: $($commandStatus.status)..." -ForegroundColor Gray
            }
        }

        if (-not $completed) {
            $lastStatus = "unknown"
            try { $lastStatus = (Invoke-LidarrApi -Endpoint "command/$($command.id)").status } catch {}
            $result.Errors += "ImportListSync command timed out (last status: $lastStatus)"

            # Structured timeout details (explicit at source)
            $timeoutDetails = New-ApiTimeoutDetails `
                -TimeoutType 'commandPoll' `
                -TimeoutSeconds $CommandTimeoutSec `
                -Endpoint "/api/v1/command/$($command.id)" `
                -Operation 'ImportListSync' `
                -PluginName 'Brainarr' `
                -Phase 'BrainarrLLM:PollCommand' `
                -CommandId $command.id
            foreach ($key in $timeoutDetails.Keys) {
                $result.Details[$key] = $timeoutDetails[$key]
            }

            return $result
        }

        $result.Details.syncCompleted = $true

        # Step 5: Verify import list is still accessible and has no error
        Write-Host "       Verifying import list post-sync..." -ForegroundColor Gray

        $postSync = $null
        try {
            $postSync = Invoke-LidarrApi -Endpoint "importlist/$($brainarrList.id)"
        }
        catch {
            $result.Errors += "Import list not accessible after sync: $_"
            return $result
        }

        # Check for lastSyncError if exposed by API
        $syncError = $null
        if ($postSync.PSObject.Properties['lastSyncError']) {
            $syncError = $postSync.lastSyncError
        }
        if ($syncError -and $syncError -ne '') {
            $result.Details.lastSyncError = $syncError
            $result.Errors += "Import list sync error: $syncError"
            return $result
        }

        # Record lastSync timestamp if available
        if ($postSync.PSObject.Properties['lastSync']) {
            $result.Details.lastSync = $postSync.lastSync
        }

        Write-Host "       Import list verified - no sync errors" -ForegroundColor Green

        $result.Success = $true
        $result.Outcome = 'success'
    }
    catch {
        $errMsg = "$_"
        # Check if exception is LLM/network related
        if ($errMsg -match '(?i)(connection refused|timeout|unreachable|network)') {
            if ($StrictMode) {
                $result.Errors += "LLM endpoint error: $errMsg"
            } else {
                $result.Outcome = 'skipped'
                $result.SkipReason = "LLM endpoint error: $errMsg"
            }
            return $result
        }
        $result.Errors += "BrainarrLLM gate failed: $errMsg"
    }

    return $result
}

function New-ApiTimeoutDetails {
    <#
    .SYNOPSIS
        Creates structured details for E2E_API_TIMEOUT error code.

    .DESCRIPTION
        Single source of truth for the API_TIMEOUT details structure.
        All gates must call this helper to prevent structural drift.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet('http', 'commandPoll', 'queuePoll', 'queueCompletion')]
        [string]$TimeoutType,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds,

        [Parameter(Mandatory)]
        [string]$Endpoint,

        [Parameter(Mandatory)]
        [string]$Operation,

        [Parameter(Mandatory)]
        [string]$PluginName,

        [Parameter(Mandatory)]
        [string]$Phase,

        [int]$IndexerId,
        [int]$DownloadClientId,
        [int]$CommandId,
        [int]$Attempts,
        [int]$ElapsedMs
    )

    # Normalize endpoint: strip scheme+host, keep path+query, redact sensitive query params
    $normalizedEndpoint = $Endpoint
    if ($Endpoint -match '^https?://') {
        # Full URL - extract path+query only
        try {
            $uri = [System.Uri]$Endpoint
            $normalizedEndpoint = $uri.PathAndQuery
        }
        catch {
            # If URI parsing fails, try regex extraction
            $normalizedEndpoint = $Endpoint -replace '^https?://[^/]+', ''
        }
    }
    # Redact sensitive query parameters (apiKey, token, password, secret, etc.)
    $normalizedEndpoint = $normalizedEndpoint -replace '(?i)(apiKey|token|password|secret|key|auth)=[^&]+', '$1=[REDACTED]'

    $details = @{
        ErrorCode = 'E2E_API_TIMEOUT'
        timeoutType = $TimeoutType
        timeoutSeconds = $TimeoutSeconds
        endpoint = $normalizedEndpoint
        operation = $Operation
        pluginName = $PluginName
        phase = $Phase
    }

    # Add optional fields only when provided (non-zero)
    if ($IndexerId -and $IndexerId -gt 0) {
        $details.indexerId = $IndexerId
    }
    if ($DownloadClientId -and $DownloadClientId -gt 0) {
        $details.downloadClientId = $DownloadClientId
    }
    if ($CommandId -and $CommandId -gt 0) {
        $details.commandId = $CommandId
    }
    if ($Attempts -and $Attempts -gt 0) {
        $details.attempts = $Attempts
    }
    if ($ElapsedMs -and $ElapsedMs -gt 0) {
        $details.elapsedMs = $ElapsedMs
    }

    return $details
}

function New-ImportFailedDetails {
    <#
    .SYNOPSIS
        Creates structured details for E2E_IMPORT_FAILED error code.

    .DESCRIPTION
        Single source of truth for the IMPORT_FAILED details structure.
        Used when ImportListSync fails at command creation, command execution, or post-sync verification.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PluginName,

        [Parameter(Mandatory)]
        [int]$ImportListId,

        [Parameter(Mandatory)]
        [ValidateSet('ImportList:TriggerCommand', 'ImportList:PollCommand', 'ImportList:PostSyncVerify')]
        [string]$Phase,

        [Parameter(Mandatory)]
        [string]$Endpoint,

        [string]$ImportListName,
        [int]$CommandId,
        [string]$CommandStatus,
        [string]$LastSyncError,
        [bool]$PostSyncVerified = $false,
        [bool]$PreSyncImportListFound = $true,
        [int]$ElapsedMs,
        [int]$Attempts
    )

    # Normalize endpoint: strip scheme+host, redact sensitive query params
    $normalizedEndpoint = $Endpoint
    if ($Endpoint -match '^https?://') {
        try {
            $uri = [System.Uri]$Endpoint
            $normalizedEndpoint = $uri.PathAndQuery
        }
        catch {
            $normalizedEndpoint = $Endpoint -replace '^https?://[^/]+', ''
        }
    }
    $normalizedEndpoint = $normalizedEndpoint -replace '(?i)(apiKey|token|password|secret|key|auth)=[^&]+', '$1=[REDACTED]'

    # Sanitize lastSyncError (may contain URLs with secrets)
    $sanitizedLastSyncError = $LastSyncError
    if ($LastSyncError) {
        # Redact full URLs
        $sanitizedLastSyncError = $LastSyncError -replace 'https?://[^\s"'']+', '[REDACTED-URL]'
        # Redact query params that might contain secrets
        $sanitizedLastSyncError = $sanitizedLastSyncError -replace '(?i)(apiKey|token|password|secret|key|auth)=[^\s&"'']+', '$1=[REDACTED]'
    }

    $details = @{
        ErrorCode = 'E2E_IMPORT_FAILED'
        pluginName = $PluginName
        importListId = $ImportListId
        operation = 'ImportListSync'
        phase = $Phase
        endpoint = $normalizedEndpoint
        postSyncVerified = $PostSyncVerified
        preSyncImportListFound = $PreSyncImportListFound
    }

    # Add optional fields only when provided
    if (-not [string]::IsNullOrWhiteSpace($ImportListName)) {
        $details.importListName = $ImportListName
    }
    if ($CommandId -and $CommandId -gt 0) {
        $details.commandId = $CommandId
    }
    if (-not [string]::IsNullOrWhiteSpace($CommandStatus)) {
        $details.commandStatus = $CommandStatus
    }
    if (-not [string]::IsNullOrWhiteSpace($sanitizedLastSyncError)) {
        $details.lastSyncError = $sanitizedLastSyncError
    }
    if ($ElapsedMs -and $ElapsedMs -gt 0) {
        $details.elapsedMs = $ElapsedMs
    }
    if ($Attempts -and $Attempts -gt 0) {
        $details.attempts = $Attempts
    }

    return $details
}


function New-ConfigInvalidDetails {
    <#
    .SYNOPSIS
        Creates structured details for E2E_CONFIG_INVALID error code.

    .DESCRIPTION
        Single source of truth for the CONFIG_INVALID details structure.
        Used when component create/update fails due to validation errors,
        schema mismatch, or API rejection.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PluginName,

        [Parameter(Mandatory)]
        [ValidateSet('indexer', 'downloadClient', 'importList')]
        [string]$ComponentType,

        [Parameter(Mandatory)]
        [ValidateSet('create', 'update')]
        [string]$Operation,

        [Parameter(Mandatory)]
        [string]$Endpoint,

        [Parameter(Mandatory)]
        [ValidateSet('Configure:Create:Post', 'Configure:Update:Put', 'Configure:SchemaMismatch', 'Configure:ValidationFailed')]
        [string]$Phase,

        [int]$HttpStatus,
        [string[]]$ValidationErrors = @(),
        [string[]]$FieldNames = @(),
        [int]$ComponentId,
        [string]$SchemaContract,
        [int]$MaxErrors = 10,
        [int]$MaxFields = 10
    )

    # Normalize endpoint: strip scheme+host, redact sensitive query params
    $normalizedEndpoint = $Endpoint
    if ($Endpoint -match '^https?://') {
        try {
            $uri = [System.Uri]$Endpoint
            $normalizedEndpoint = $uri.PathAndQuery
        }
        catch {
            $normalizedEndpoint = $Endpoint -replace '^https?://[^/]+', ''
        }
    }
    $normalizedEndpoint = $normalizedEndpoint -replace '(?i)(apiKey|token|password|secret|key|auth)=[^&]+', '$1=[REDACTED]'

    # Sanitize validation errors (may contain URLs with secrets or PII)
    $sanitizedErrors = @()
    foreach ($err in @($ValidationErrors)) {
        if ([string]::IsNullOrWhiteSpace($err)) { continue }
        $sanitized = $err
        # Redact URLs
        $sanitized = $sanitized -replace 'https?://[^\s"'']+', '[REDACTED-URL]'
        # Redact query params that might contain secrets
        $sanitized = $sanitized -replace '(?i)(apiKey|token|password|secret|key|auth|userId|email)=[^\s&"'']+', '$1=[REDACTED]'
        $sanitizedErrors += $sanitized
    }

    # Cap and sort validation errors deterministically
    $totalErrors = $sanitizedErrors.Count
    $errorsCapped = $totalErrors -gt $MaxErrors
    $sortedErrors = @($sanitizedErrors | Sort-Object { $_.ToUpperInvariant() })
    $cappedErrors = if ($errorsCapped) { @($sortedErrors | Select-Object -First $MaxErrors) } else { $sortedErrors }

    # Cap and sort field names deterministically
    $cleanFieldNames = @($FieldNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $totalFields = $cleanFieldNames.Count
    $fieldsCapped = $totalFields -gt $MaxFields
    $sortedFields = @($cleanFieldNames | Sort-Object { $_.ToUpperInvariant() })
    $cappedFields = if ($fieldsCapped) { @($sortedFields | Select-Object -First $MaxFields) } else { $sortedFields }

    $details = @{
        ErrorCode = 'E2E_CONFIG_INVALID'
        pluginName = $PluginName
        componentType = $ComponentType
        operation = $Operation
        endpoint = $normalizedEndpoint
        phase = $Phase
        validationErrors = @($cappedErrors)
        validationErrorCount = $totalErrors
        validationErrorsCapped = $errorsCapped
        fieldNames = @($cappedFields)
        fieldNameCount = $totalFields
        fieldNamesCapped = $fieldsCapped
    }

    # Add optional fields only when provided
    if ($HttpStatus -and $HttpStatus -gt 0) {
        $details.httpStatus = $HttpStatus
    }
    if ($ComponentId -and $ComponentId -gt 0) {
        $details.componentId = $ComponentId
    }
    if (-not [string]::IsNullOrWhiteSpace($SchemaContract)) {
        $details.schemaContract = $SchemaContract
    }

    return $details
}


function Get-FoundIndexerNamesDetails {
    <#
    .SYNOPSIS
        Produces a capped, deterministic summary of indexer names present in a Lidarr release list.

    .DESCRIPTION
        Used for E2E_NO_RELEASES_ATTRIBUTED diagnostics to prevent unbounded output/log growth.
        - Dedupe is case-insensitive (OrdinalIgnoreCase)
        - Sort is culture-invariant (ToUpperInvariant)
        - Output is capped to MaxNames, and includes pre-cap count + capped flag
    #>
    param(
        [Parameter(Mandatory)]
        [object[]]$Releases,
        [int]$MaxNames = 10
    )

    if ($MaxNames -lt 1) { $MaxNames = 1 }

    $set = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $names = [System.Collections.Generic.List[string]]::new()

    foreach ($item in @($Releases)) {
        $name = $null

        if ($item -is [string]) {
            $name = $item
        }
        elseif ($item -is [hashtable]) {
            $name = $item['indexer']
        }
        else {
            try { $name = $item.indexer } catch { $name = $null }
        }

        $name = ([string]$name).Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }

        if ($set.Add($name)) {
            $names.Add($name)
        }
    }

    $sorted = @($names | Sort-Object { $_.ToUpperInvariant() })
    $totalUnique = $sorted.Count
    $capped = $totalUnique -gt $MaxNames

    $cappedNames = if ($capped) { @($sorted | Select-Object -First $MaxNames) } else { $sorted }

    return [PSCustomObject]@{
        foundIndexerNames = @($cappedNames)
        foundIndexerNameCount = $totalUnique
        foundIndexerNamesCapped = $capped
    }
}

Export-ModuleMember -Function Initialize-E2EGates, Test-PackagingPreflight, Test-SchemaGate, Test-SearchGate, Test-IsCredentialPrereqSkipReason, Test-AlbumSearchGate, Test-PluginGrabGate, Test-GrabGate, Test-ImportListGate, Test-MetadataGate, Test-AudioFileValidation, Test-LLMEndpoint, Test-LLMModelAvailability, Test-BrainarrLLMGate, Get-FoundIndexerNamesDetails, New-ApiTimeoutDetails, New-ImportFailedDetails, New-ConfigInvalidDetails, Invoke-LidarrApi
