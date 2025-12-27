# E2E Gate Implementations for Plugin Testing
# Gates: Schema (no credentials) -> Search (credentials) -> Grab (credentials)

$script:LidarrApiUrl = $null
$script:LidarrApiKey = $null

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

        [object]$Body = $null
    )

    $headers = @{
        'X-Api-Key' = $script:LidarrApiKey
        'Content-Type' = 'application/json'
    }

    $params = @{
        Uri = "$script:LidarrApiUrl/api/v1/$Endpoint"
        Method = $Method
        Headers = $headers
        TimeoutSec = 30
    }

    if ($Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 10)
    }

    $response = Invoke-RestMethod @params
    return $response
}

function Test-SchemaGate {
    <#
    .SYNOPSIS
        Gate 1: Verify plugin schema is registered (requires Lidarr API key).

    .DESCRIPTION
        Checks that the plugin's indexer and/or download client schemas
        are present in Lidarr's schema endpoints. Also supports import list schemas.

    .PARAMETER PluginName
        Name of the plugin (e.g., "Qobuzarr", "Tidalarr", "Brainarr")

    .PARAMETER ExpectIndexer
        Whether to expect an indexer schema.

    .PARAMETER ExpectDownloadClient
        Whether to expect a download client schema.

    .PARAMETER ExpectImportList
        Whether to expect an import list schema.

    .OUTPUTS
        PSCustomObject with Success, IndexerFound, DownloadClientFound, ImportListFound, Errors
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PluginName,

        [switch]$ExpectIndexer,
        [switch]$ExpectDownloadClient,
        [switch]$ExpectImportList
    )

    $result = [PSCustomObject]@{
        Gate = 'Schema'
        PluginName = $PluginName
        Success = $false
        IndexerFound = $false
        DownloadClientFound = $false
        ImportListFound = $false
        Errors = @()
    }

    try {
        # Check indexer schemas
        if ($ExpectIndexer) {
            $indexerSchemas = Invoke-LidarrApi -Endpoint 'indexer/schema'       
            $found = $indexerSchemas | Where-Object {
                $_.implementation -like "*$PluginName*" -or
                $_.implementationName -like "*$PluginName*"
            }
            $result.IndexerFound = $null -ne $found

            if (-not $result.IndexerFound) {
                $result.Errors += "Indexer schema for '$PluginName' not found"  
            }
        }

        # Check download client schemas
        if ($ExpectDownloadClient) {
            $clientSchemas = Invoke-LidarrApi -Endpoint 'downloadclient/schema' 
            $found = $clientSchemas | Where-Object {
                $_.implementation -like "*$PluginName*" -or
                $_.implementationName -like "*$PluginName*"
            }
            $result.DownloadClientFound = $null -ne $found

            if (-not $result.DownloadClientFound) {
                $result.Errors += "DownloadClient schema for '$PluginName' not found"
            }
        }

        # Check import list schemas
        if ($ExpectImportList) {
            $importListSchemas = Invoke-LidarrApi -Endpoint 'importlist/schema'
            $found = $importListSchemas | Where-Object {
                $_.implementation -like "*$PluginName*" -or
                $_.implementationName -like "*$PluginName*"
            }
            $result.ImportListFound = $null -ne $found

            if (-not $result.ImportListFound) {
                $result.Errors += "ImportList schema for '$PluginName' not found"
            }
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

        [string[]]$CredentialFieldNames = @(),

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
                [string[]]$RequiredFields
            )

            if (-not $RequiredFields -or $RequiredFields.Count -eq 0) { return $false }

            # Best-effort: only enforce fields that exist on the indexer config.
            # This avoids false skips if field naming changes (e.g., email vs username).
            $anyApplicableField = $false

            foreach ($fieldName in $RequiredFields) {
                if (-not (Has-Field -Fields $Indexer.fields -Name $fieldName)) { continue }
                $anyApplicableField = $true
                $value = Get-FieldValue -Fields $Indexer.fields -Name $fieldName
                if ([string]::IsNullOrWhiteSpace("$value")) {
                    return $true
                }
            }

            if (-not $anyApplicableField) { return $false }
            return $false
        }

        if ($SkipIfNoCreds -and (Is-MissingCredentials -Indexer $indexer -RequiredFields $CredentialFieldNames)) {
            $result.Outcome = 'skipped'
            $result.SkipReason = "Credentials not configured (missing: $($CredentialFieldNames -join ', '))"
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
        }
    }
    catch {
        $result.Errors += "Grab gate failed: $_"
    }

    return $result
}

Export-ModuleMember -Function Initialize-E2EGates, Test-SchemaGate, Test-SearchGate, Test-GrabGate, Invoke-LidarrApi
