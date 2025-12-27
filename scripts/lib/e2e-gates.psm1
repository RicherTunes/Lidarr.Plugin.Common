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
        Gate 1: Verify plugin schema is registered (no credentials required).

    .DESCRIPTION
        Checks that the plugin's indexer and/or download client schemas
        are present in Lidarr's schema endpoints.

    .PARAMETER PluginName
        Name of the plugin (e.g., "Qobuzarr", "Tidalarr", "Brainarr")

    .PARAMETER ExpectIndexer
        Whether to expect an indexer schema.

    .PARAMETER ExpectDownloadClient
        Whether to expect a download client schema.

    .OUTPUTS
        PSCustomObject with Success, IndexerFound, DownloadClientFound, Errors
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PluginName,

        [switch]$ExpectIndexer,
        [switch]$ExpectDownloadClient
    )

    $result = [PSCustomObject]@{
        Gate = 'Schema'
        PluginName = $PluginName
        Success = $false
        IndexerFound = $false
        DownloadClientFound = $false
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
        results are returned.

    .PARAMETER IndexerId
        ID of the configured indexer to test.

    .PARAMETER SearchQuery
        Optional search query (defaults to "Miles Davis")

    .OUTPUTS
        PSCustomObject with Success, ResultCount, Errors
    #>
    param(
        [Parameter(Mandatory)]
        [int]$IndexerId,

        [string]$SearchQuery = "Miles Davis"
    )

    $result = [PSCustomObject]@{
        Gate = 'Search'
        IndexerId = $IndexerId
        Success = $false
        ResultCount = 0
        Errors = @()
    }

    try {
        # Get indexer info
        $indexer = Invoke-LidarrApi -Endpoint "indexer/$IndexerId"

        if (-not $indexer) {
            $result.Errors += "Indexer $IndexerId not found"
            return $result
        }

        # Test the indexer
        $testResult = Invoke-LidarrApi -Endpoint "indexer/test" -Method POST -Body @{
            id = $IndexerId
            name = $indexer.name
            implementation = $indexer.implementation
            configContract = $indexer.configContract
            fields = $indexer.fields
        }

        if ($testResult.isValid -eq $false) {
            $result.Errors += "Indexer test failed: $($testResult.validationFailures | ConvertTo-Json)"
            return $result
        }

        # Perform a search
        $searchResults = Invoke-LidarrApi -Endpoint "search?term=$([Uri]::EscapeDataString($SearchQuery))"
        $result.ResultCount = ($searchResults | Measure-Object).Count
        $result.Success = $result.ResultCount -gt 0

        if (-not $result.Success) {
            $result.Errors += "Search returned no results for '$SearchQuery'"
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
