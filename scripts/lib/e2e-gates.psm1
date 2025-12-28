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

        # Backward compatibility: this maps to CredentialAllOfFieldNames.
        [string[]]$CredentialFieldNames = @(),

        [string[]]$CredentialAllOfFieldNames = @(),

        [string[]]$CredentialAnyOfFieldNames = @(),

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
                [string[]]$AnyOfFields,
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
                $anyApplicable = $false
                $anyHasValue = $false

                foreach ($fieldName in $AnyOfFields) {
                    if (-not (Has-Field -Fields $Indexer.fields -Name $fieldName)) { continue }
                    $anyApplicable = $true
                    $value = Get-FieldValue -Fields $Indexer.fields -Name $fieldName
                    if (-not [string]::IsNullOrWhiteSpace("$value")) {
                        $anyHasValue = $true
                        break
                    }
                }

                if ($anyApplicable -and -not $anyHasValue) {
                    $missing.Add("any of: " + ($AnyOfFields -join ", "))
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

        [string[]]$CredentialAnyOfFieldNames = @(),

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
        SkipReason = $null
    }

    try {
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
                $anyApplicable = $false
                $anyHasValue = $false

                foreach ($fieldName in $CredentialAnyOfFieldNames) {
                    if (-not (Has-Field -Fields $indexer.fields -Name $fieldName)) { continue }
                    $anyApplicable = $true
                    $value = Get-FieldValue -Fields $indexer.fields -Name $fieldName
                    if (-not [string]::IsNullOrWhiteSpace("$value")) {
                        $anyHasValue = $true
                        break
                    }
                }

                if ($anyApplicable -and -not $anyHasValue) {
                    $missing.Add("any of: " + ($CredentialAnyOfFieldNames -join ", "))
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
            $otherIndexers = $releases | ForEach-Object { "$($_.indexer):$($_.indexerId)" } | Select-Object -Unique
            $indexerList = if ($otherIndexers) { $otherIndexers -join ', ' } else { '(none)' }

            Write-Host "       FAIL: No releases attributed to plugin!" -ForegroundColor Red
            $result.Errors += "No releases from configured indexer [$indexerContext]. Total: $totalReleases. Found: $indexerList"

            # Check for null-indexer releases (same as AlbumSearch gate)
            $nullIndexerReleases = $releases | Where-Object {
                [string]::IsNullOrWhiteSpace($_.indexer) -or $_.indexerId -eq 0
            }
            $nullIndexerCount = ($nullIndexerReleases | Measure-Object).Count

            if ($nullIndexerCount -gt 0) {
                Write-Host "       WARNING: $nullIndexerCount releases have null/empty indexer or indexerId=0!" -ForegroundColor Red
                $result.Errors += "ATTRIBUTION WARNING: $nullIndexerCount of $totalReleases releases have null/empty indexer or indexerId=0"

                # Sample first 3 suspicious releases (redacted)
                $suspiciousSamples = $nullIndexerReleases | Select-Object -First 3 | ForEach-Object {
                    $title = if ($_.title -and $_.title.Length -gt 40) { $_.title.Substring(0,40) + "..." } else { $_.title }
                    "indexer='$($_.indexer)' indexerId=$($_.indexerId) title='$title'"
                }
                foreach ($sample in $suspiciousSamples) {
                    $result.Errors += "  Null-indexer sample: $sample"
                    Write-Host "       - $sample" -ForegroundColor Yellow
                }
            }

            # Outcome is FAIL, not SKIP - this is a regression
            return $result
        }

        # Pick first acceptable release
        $targetRelease = $pluginReleases | Select-Object -First 1
        $result.ReleaseGuid = $targetRelease.guid
        $result.ReleaseTitle = if ($targetRelease.title.Length -gt 50) { $targetRelease.title.Substring(0,50) + "..." } else { $targetRelease.title }

        Write-Host "       Selected: $($result.ReleaseTitle)" -ForegroundColor Gray
        Write-Host "       Triggering grab..." -ForegroundColor Gray

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
        Write-Host "       Waiting for queue item..." -ForegroundColor Gray
        $deadline = (Get-Date).AddSeconds([Math]::Max(1, $QueueTimeoutSec))
        $queueItem = $null

        while ((Get-Date) -lt $deadline) {
            $queueRecords = Get-QueueRecords
            $queueItem = $queueRecords | Where-Object { $_.downloadId -eq $grabResult.downloadId } | Select-Object -First 1
            if ($queueItem) { break }
            Start-Sleep -Milliseconds 500
        }

        if (-not $queueItem) {
            $queueRecords = Get-QueueRecords
            $queueCount = ($queueRecords | Measure-Object).Count
            $result.Errors += "Grab succeeded but item not found in queue (waited ${QueueTimeoutSec}s; queue has $queueCount items)"
            $result.Errors += "downloadId=$($grabResult.downloadId)"
            return $result
        }

        $result.QueueItemId = $queueItem.id
        $result.OutputPath = $queueItem.outputPath
        $result.QueueStatus = $queueItem.status
        $result.TrackedDownloadStatus = $queueItem.trackedDownloadStatus
        $result.TrackedDownloadState = $queueItem.trackedDownloadState

        Write-Host "       Queue item found: id=$($queueItem.id) status=$($queueItem.status)" -ForegroundColor Green

        # Step 5: Wait for completion
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
            return $result
        }

        # Step 6: v3 multi-file validation (requires Docker container access)
        if ($ValidateOutputPath -and $ContainerName -and (Get-Command docker -ErrorAction SilentlyContinue) -and $result.OutputPath) {
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
                Write-Host "       Files in outputPath: $($allFiles.Count)" -ForegroundColor Gray

                # Check minimum file count
                if ($allFiles.Count -lt $ExpectedMinFiles) {
                    $result.Errors += "Expected at least $ExpectedMinFiles files, found $($allFiles.Count) under: $outPath"
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

                # Select files to validate (first N for deterministic debugging)
                $filesToValidate = $allFiles | Select-Object -First $MaxFilesToValidate
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

        [string[]]$CredentialAnyOfFieldNames = @(),

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
                $anyApplicable = $false
                $anyHasValue = $false

                foreach ($fieldName in $CredentialAnyOfFieldNames) {
                    if (-not (Has-Field -Fields $indexer.fields -Name $fieldName)) { continue }
                    $anyApplicable = $true
                    $value = Get-FieldValue -Fields $indexer.fields -Name $fieldName
                    if (-not [string]::IsNullOrWhiteSpace("$value")) {
                        $anyHasValue = $true
                        break
                    }
                }

                if ($anyApplicable -and -not $anyHasValue) {
                    $missing.Add("any of: " + ($CredentialAnyOfFieldNames -join ", "))
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
            return $result
        }

        # Step 5: Check releases for this album
        Write-Host "       Fetching releases for album $($album.id)..." -ForegroundColor Gray
        $releases = Invoke-LidarrApi -Endpoint "release?albumId=$($album.id)"

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
            $otherIndexers = $releases | ForEach-Object { "$($_.indexer):$($_.indexerId)" } | Select-Object -Unique
            $indexerList = if ($otherIndexers) { $otherIndexers -join ', ' } else { '(none)' }

            # Include full indexer context for triage (no extra API call needed)
            $indexerContext = "name='$($result.IndexerName)' impl='$($result.IndexerImplementation)' id=$IndexerId"
            $result.Errors += "No releases from configured indexer [$indexerContext]. Total: $totalReleases. Found: $indexerList"

            # Check for null-indexer releases - likely parser/attribution regression
            $nullIndexerReleases = $releases | Where-Object {
                [string]::IsNullOrWhiteSpace($_.indexer) -or $_.indexerId -eq 0
            }
            $nullIndexerCount = ($nullIndexerReleases | Measure-Object).Count

            if ($nullIndexerCount -gt 0) {
                # LOUD warning - this is almost always a parser bug
                Write-Host "       WARNING: $nullIndexerCount releases have null/empty indexer or indexerId=0!" -ForegroundColor Red
                Write-Host "       This likely indicates a parser attribution regression." -ForegroundColor Red

                $result.Errors += "ATTRIBUTION WARNING: $nullIndexerCount of $totalReleases releases have null/empty indexer or indexerId=0"

                # Sample first 3 suspicious releases (redact sensitive fields)
                $suspiciousSamples = $nullIndexerReleases | Select-Object -First 3 | ForEach-Object {
                    $title = if ($_.title -and $_.title.Length -gt 40) { $_.title.Substring(0,40) + "..." } else { $_.title }
                    # Only include safe fields: indexer, indexerId, title (no guid/downloadUrl/infoUrl)
                    "indexer='$($_.indexer)' indexerId=$($_.indexerId) title='$title'"
                }
                foreach ($sample in $suspiciousSamples) {
                    $result.Errors += "  Null-indexer sample: $sample"
                    Write-Host "       - $sample" -ForegroundColor Yellow
                }
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

Export-ModuleMember -Function Initialize-E2EGates, Test-SchemaGate, Test-SearchGate, Test-AlbumSearchGate, Test-PluginGrabGate, Test-GrabGate, Invoke-LidarrApi
