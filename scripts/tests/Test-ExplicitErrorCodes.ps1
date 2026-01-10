$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Explicit ErrorCodes (Queue + Metadata + ZeroAudio)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$jsonModule = Join-Path $repoRoot 'scripts/lib/e2e-json-output.psm1'
$gatesModule = Join-Path $repoRoot 'scripts/lib/e2e-gates.psm1'

if (-not (Test-Path $jsonModule)) {
    throw "Module not found: $jsonModule"
}
if (-not (Test-Path $gatesModule)) {
    throw "Module not found: $gatesModule"
}

Import-Module $jsonModule -Force
Import-Module $gatesModule -Force

$passed = 0
$failed = 0

function Assert-True {
    param([string]$Name, [bool]$Condition)
    if (-not $Condition) {
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        $script:failed++
    }
    else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

function Assert-Equal {
    param([string]$Name, $Actual, $Expected)
    if ($Actual -ne $Expected) {
        Write-Host "  [FAIL] $Name (expected=$Expected actual=$Actual)" -ForegroundColor Red
        $script:failed++
    }
    else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

Write-Host "`nTest Group: ErrorCode set in Details becomes top-level errorCode" -ForegroundColor Yellow

$queueFail = [PSCustomObject]@{
    Gate = 'Grab'
    PluginName = 'Tidalarr'
    Outcome = 'failed'
    Errors = @('Grab succeeded but item not found in queue (waited 60s; queue has 3 items)')
    Details = @{
        ErrorCode = 'E2E_QUEUE_NOT_FOUND'
        QueueTimeoutSec = 60
        QueueCount = 3
    }
}

$metaFail = [PSCustomObject]@{
    Gate = 'Metadata'
    PluginName = 'Qobuzarr'
    Outcome = 'failed'
    Errors = @("E2E_METADATA_MISSING: Audio file '01 - So What.flac' missing required tags")
    Details = @{
        ErrorCode = 'E2E_METADATA_MISSING'
        MissingTags = @('01 - So What.flac: Missing tags: artist, album')
        SampleFile = '01 - So What.flac'
    }
}

$zeroAudioFail = [PSCustomObject]@{
    Gate = 'PostRestartGrab'
    PluginName = 'Tidalarr'
    Outcome = 'failed'
    Errors = @('E2E_ZERO_AUDIO_FILES: No audio files found in: /downloads/tidalarr')
    Details = @{
        ErrorCode = 'E2E_ZERO_AUDIO_FILES'
        OutputPath = '/downloads/tidalarr'
        TotalFilesFound = 0
        ValidatedFiles = @()
        ValidationPhase = 'grab:fileValidation'
    }
}

# CRITICAL: Metadata gate "0 files" should emit E2E_ZERO_AUDIO_FILES, NOT E2E_METADATA_MISSING
$metadataZeroFilesFail = [PSCustomObject]@{
    Gate = 'Metadata'
    PluginName = 'Tidalarr'
    Outcome = 'failed'
    Errors = @('E2E_ZERO_AUDIO_FILES: No audio files found in output path: /downloads/tidalarr')
    Details = @{
        ErrorCode = 'E2E_ZERO_AUDIO_FILES'
        OutputPath = '/downloads/tidalarr'
        TotalFilesChecked = 0
        ValidationPhase = 'metadata:containerScan'
    }
}

$json = ConvertTo-E2ERunManifest -Results @($queueFail, $metaFail, $zeroAudioFail, $metadataZeroFilesFail) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}

$m = $json | ConvertFrom-Json

$queueResult = $m.results | Where-Object { $_.gate -eq 'Grab' -and $_.plugin -eq 'Tidalarr' } | Select-Object -First 1
$metaResult = $m.results | Where-Object { $_.gate -eq 'Metadata' -and $_.plugin -eq 'Qobuzarr' } | Select-Object -First 1
$zeroAudioResult = $m.results | Where-Object { $_.gate -eq 'PostRestartGrab' -and $_.plugin -eq 'Tidalarr' } | Select-Object -First 1

Assert-Equal "Grab errorCode is E2E_QUEUE_NOT_FOUND" $queueResult.errorCode 'E2E_QUEUE_NOT_FOUND'
Assert-True "Grab details has no errorCode key (moved to top-level)" (-not ($queueResult.details.PSObject.Properties.Name -contains 'errorCode'))

Assert-Equal "Metadata errorCode is E2E_METADATA_MISSING" $metaResult.errorCode 'E2E_METADATA_MISSING'
Assert-True "Metadata details has missingTags array" (@(@($metaResult.details.missingTags) | Where-Object { $null -ne $_ }).Count -gt 0)
Assert-Equal "Metadata details.sampleFile preserved" $metaResult.details.sampleFile '01 - So What.flac'
Assert-True "Metadata details has no errorCode key (moved to top-level)" (-not ($metaResult.details.PSObject.Properties.Name -contains 'errorCode'))

Assert-Equal "ZeroAudio errorCode is E2E_ZERO_AUDIO_FILES" $zeroAudioResult.errorCode 'E2E_ZERO_AUDIO_FILES'
Assert-Equal "ZeroAudio details.totalFilesFound is 0" $zeroAudioResult.details.totalFilesFound 0
Assert-True "ZeroAudio details.outputPath preserved" ($zeroAudioResult.details.outputPath -eq '/downloads/tidalarr')
Assert-True "ZeroAudio details has no errorCode key (moved to top-level)" (-not ($zeroAudioResult.details.PSObject.Properties.Name -contains 'errorCode'))
Assert-Equal "ZeroAudio details.validationPhase is grab:fileValidation" $zeroAudioResult.details.validationPhase 'grab:fileValidation'

# CRITICAL: Metadata gate "0 files" must NOT use E2E_METADATA_MISSING
$metadataZeroFilesResult = $m.results | Where-Object { $_.gate -eq 'Metadata' -and $_.plugin -eq 'Tidalarr' } | Select-Object -First 1
Assert-Equal "MetadataZeroFiles errorCode is E2E_ZERO_AUDIO_FILES (not E2E_METADATA_MISSING)" $metadataZeroFilesResult.errorCode 'E2E_ZERO_AUDIO_FILES'
Assert-Equal "MetadataZeroFiles details.totalFilesChecked is 0" $metadataZeroFilesResult.details.totalFilesChecked 0
Assert-Equal "MetadataZeroFiles details.validationPhase is metadata:containerScan" $metadataZeroFilesResult.details.validationPhase 'metadata:containerScan'
Assert-True "MetadataZeroFiles details has no errorCode key (moved to top-level)" (-not ($metadataZeroFilesResult.details.PSObject.Properties.Name -contains 'errorCode'))

Write-Host "`nTest Group: SampleFile Guarantee (P0.3)" -ForegroundColor Yellow

# INVARIANT: If MissingTags.Count > 0, SampleFile must be non-empty
# Test that metadata result with missing tags has sampleFile set
$metaWithMissing = $m.results | Where-Object { $_.gate -eq 'Metadata' -and $_.plugin -eq 'Qobuzarr' } | Select-Object -First 1
$hasMissingTags = @($metaWithMissing.details.missingTags).Count -gt 0
$hasSampleFile = -not [string]::IsNullOrWhiteSpace($metaWithMissing.details.sampleFile)

Assert-True "Metadata with missingTags has sampleFile set" ($hasMissingTags -and $hasSampleFile)
Assert-True "SampleFile invariant: missingTags > 0 implies sampleFile non-empty" (-not $hasMissingTags -or $hasSampleFile)

# Test case: error reading tags should also set sampleFile
$metaErrorReading = [PSCustomObject]@{
    Gate = 'Metadata'
    PluginName = 'Brainarr'
    Outcome = 'failed'
    Errors = @("E2E_METADATA_MISSING: Audio file 'track01.flac' error reading tags")
    Details = @{
        ErrorCode = 'E2E_METADATA_MISSING'
        MissingTags = @('track01.flac: Error reading tags: Cannot read file')
        SampleFile = 'track01.flac'  # MUST be set even for read errors
    }
}

$jsonWithError = ConvertTo-E2ERunManifest -Results @($metaErrorReading) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}
$mError = $jsonWithError | ConvertFrom-Json
$errorResult = $mError.results | Where-Object { $_.gate -eq 'Metadata' -and $_.plugin -eq 'Brainarr' } | Select-Object -First 1

Assert-True "Error reading tags: sampleFile is set" (-not [string]::IsNullOrWhiteSpace($errorResult.details.sampleFile))
Assert-Equal "Error reading tags: sampleFile matches first failing file" $errorResult.details.sampleFile 'track01.flac'

Write-Host "`nTest Group: E2E_NO_RELEASES_ATTRIBUTED (P1)" -ForegroundColor Yellow

$noReleasesAttrFail = [PSCustomObject]@{
    Gate = 'Search'
    PluginName = 'Qobuzarr'
    Outcome = 'failed'
    Errors = @('E2E_NO_RELEASES_ATTRIBUTED: AlbumSearch returned 15 releases but 0 attributed to Qobuzarr')
    Details = @{
        ErrorCode = 'E2E_NO_RELEASES_ATTRIBUTED'
        TotalReleases = 15
        AttributedReleases = 0
        ExpectedIndexerName = 'Qobuzarr'
        ExpectedIndexerId = 101
        FoundIndexerNames = @('Usenet-Indexer', 'Torrent-Indexer')
        FoundIndexerNameCount = 2
        FoundIndexerNamesCapped = $false
        NullIndexerReleaseCount = 3
        NullIndexerSamples = @(
            @{ title = 'Kind of Blue - Miles Davis'; indexer = ''; indexerId = 0 }
        )
    }
}

$jsonNoReleases = ConvertTo-E2ERunManifest -Results @($noReleasesAttrFail) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}
$mNoReleases = $jsonNoReleases | ConvertFrom-Json
$noReleasesResult = $mNoReleases.results | Where-Object { $_.gate -eq 'Search' -and $_.plugin -eq 'Qobuzarr' } | Select-Object -First 1

Assert-Equal "NoReleasesAttr errorCode is E2E_NO_RELEASES_ATTRIBUTED" $noReleasesResult.errorCode 'E2E_NO_RELEASES_ATTRIBUTED'
Assert-True "NoReleasesAttr details has no errorCode key (moved to top-level)" (-not ($noReleasesResult.details.PSObject.Properties.Name -contains 'errorCode'))
Assert-Equal "NoReleasesAttr details.totalReleases is 15" $noReleasesResult.details.totalReleases 15
Assert-Equal "NoReleasesAttr details.attributedReleases is 0" $noReleasesResult.details.attributedReleases 0
Assert-Equal "NoReleasesAttr details.expectedIndexerName is Qobuzarr" $noReleasesResult.details.expectedIndexerName 'Qobuzarr'
Assert-Equal "NoReleasesAttr details.expectedIndexerId is 101" $noReleasesResult.details.expectedIndexerId 101
Assert-True "NoReleasesAttr details.foundIndexerNames is array" ($noReleasesResult.details.foundIndexerNames -is [array])
Assert-Equal "NoReleasesAttr details.foundIndexerNameCount is 2" $noReleasesResult.details.foundIndexerNameCount 2
Assert-Equal "NoReleasesAttr details.foundIndexerNamesCapped is false" $noReleasesResult.details.foundIndexerNamesCapped $false
Assert-Equal "NoReleasesAttr details.nullIndexerReleaseCount is 3" $noReleasesResult.details.nullIndexerReleaseCount 3
# Note: PowerShell's ConvertFrom-Json unwraps single-element arrays to objects
# So we check for presence and that it has expected properties, not array type
Assert-True "NoReleasesAttr details.nullIndexerSamples exists" ($null -ne $noReleasesResult.details.nullIndexerSamples)
$sampleObj = @($noReleasesResult.details.nullIndexerSamples)[0]
Assert-True "NoReleasesAttr nullIndexerSamples[0] has title" ($null -ne $sampleObj.title)

# Test cap behavior: 50 distinct indexer names should be capped to 10
$manyIndexersFail = [PSCustomObject]@{
    Gate = 'Search'
    PluginName = 'TestPlugin'
    Outcome = 'failed'
    Errors = @('E2E_NO_RELEASES_ATTRIBUTED: test')
    Details = @{
        ErrorCode = 'E2E_NO_RELEASES_ATTRIBUTED'
        TotalReleases = 100
        AttributedReleases = 0
        ExpectedIndexerName = 'TestPlugin'
        ExpectedIndexerId = 999
        FoundIndexerNames = @(1..50 | ForEach-Object { "Indexer-$_" })
        FoundIndexerNameCount = 50
        FoundIndexerNamesCapped = $true
        NullIndexerReleaseCount = 0
        NullIndexerSamples = @()
    }
}

$jsonManyIndexers = ConvertTo-E2ERunManifest -Results @($manyIndexersFail) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}
$mManyIndexers = $jsonManyIndexers | ConvertFrom-Json
$manyIndexersResult = $mManyIndexers.results | Where-Object { $_.plugin -eq 'TestPlugin' } | Select-Object -First 1

# Note: The cap is applied at the gate level, not in ConvertTo-E2ERunManifest
# So this test verifies the manifest preserves what's passed, and the golden fixture test verifies the cap
Assert-True "ManyIndexers details.foundIndexerNamesCapped is true" ($manyIndexersResult.details.foundIndexerNamesCapped -eq $true)
Assert-Equal "ManyIndexers details.foundIndexerNameCount is 50" $manyIndexersResult.details.foundIndexerNameCount 50

Write-Host "`nTest Group: E2E_API_TIMEOUT (P1)" -ForegroundColor Yellow

$apiTimeoutFail = [PSCustomObject]@{
    Gate = 'AlbumSearch'
    PluginName = 'Tidalarr'
    Outcome = 'failed'
    Errors = @('AlbumSearch command timed out after 120s (status=queued, message=null)')
    Details = @{
        ErrorCode = 'E2E_API_TIMEOUT'
        timeoutType = 'commandPoll'
        timeoutSeconds = 120
        endpoint = '/api/v1/command/42'
        operation = 'AlbumSearch'
        pluginName = 'Tidalarr'
        phase = 'AlbumSearch:PollCommand'
        indexerId = 101
        commandId = 42
    }
}

$jsonApiTimeout = ConvertTo-E2ERunManifest -Results @($apiTimeoutFail) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}
$mApiTimeout = $jsonApiTimeout | ConvertFrom-Json
$apiTimeoutResult = $mApiTimeout.results | Where-Object { $_.gate -eq 'AlbumSearch' -and $_.plugin -eq 'Tidalarr' } | Select-Object -First 1

Assert-Equal "ApiTimeout errorCode is E2E_API_TIMEOUT" $apiTimeoutResult.errorCode 'E2E_API_TIMEOUT'
Assert-True "ApiTimeout details has no errorCode key (moved to top-level)" (-not ($apiTimeoutResult.details.PSObject.Properties.Name -contains 'errorCode'))
Assert-Equal "ApiTimeout details.timeoutType is commandPoll" $apiTimeoutResult.details.timeoutType 'commandPoll'
Assert-Equal "ApiTimeout details.timeoutSeconds is 120" $apiTimeoutResult.details.timeoutSeconds 120
Assert-Equal "ApiTimeout details.endpoint is /api/v1/command/42" $apiTimeoutResult.details.endpoint '/api/v1/command/42'
Assert-Equal "ApiTimeout details.operation is AlbumSearch" $apiTimeoutResult.details.operation 'AlbumSearch'
Assert-Equal "ApiTimeout details.pluginName is Tidalarr" $apiTimeoutResult.details.pluginName 'Tidalarr'
Assert-Equal "ApiTimeout details.phase is AlbumSearch:PollCommand" $apiTimeoutResult.details.phase 'AlbumSearch:PollCommand'
Assert-Equal "ApiTimeout details.indexerId is 101" $apiTimeoutResult.details.indexerId 101
Assert-Equal "ApiTimeout details.commandId is 42" $apiTimeoutResult.details.commandId 42

# Test queueCompletion timeout type
$queueTimeoutFail = [PSCustomObject]@{
    Gate = 'Grab'
    PluginName = 'Qobuzarr'
    Outcome = 'failed'
    Errors = @('Queue item did not complete within 600s (status=downloading, downloadId=abc123)')
    Details = @{
        ErrorCode = 'E2E_API_TIMEOUT'
        timeoutType = 'queueCompletion'
        timeoutSeconds = 600
        endpoint = '/api/v1/queue'
        operation = 'GrabQueueWait'
        pluginName = 'Qobuzarr'
        phase = 'Grab:WaitQueueCompletion'
        indexerId = 102
    }
}

$jsonQueueTimeout = ConvertTo-E2ERunManifest -Results @($queueTimeoutFail) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}
$mQueueTimeout = $jsonQueueTimeout | ConvertFrom-Json
$queueTimeoutResult = $mQueueTimeout.results | Where-Object { $_.gate -eq 'Grab' -and $_.plugin -eq 'Qobuzarr' } | Select-Object -First 1

Assert-Equal "QueueTimeout errorCode is E2E_API_TIMEOUT" $queueTimeoutResult.errorCode 'E2E_API_TIMEOUT'
Assert-Equal "QueueTimeout details.timeoutType is queueCompletion" $queueTimeoutResult.details.timeoutType 'queueCompletion'
Assert-Equal "QueueTimeout details.timeoutSeconds is 600" $queueTimeoutResult.details.timeoutSeconds 600
Assert-Equal "QueueTimeout details.phase is Grab:WaitQueueCompletion" $queueTimeoutResult.details.phase 'Grab:WaitQueueCompletion'

Write-Host "`nTest Group: Endpoint Normalization & Redaction (New-ApiTimeoutDetails)" -ForegroundColor Yellow

# Test the helper directly to verify endpoint normalization
# Full URL with sensitive query params should be normalized to path-only with redaction
$fullUrlDetails = New-ApiTimeoutDetails `
    -TimeoutType 'http' `
    -TimeoutSeconds 30 `
    -Endpoint 'http://192.168.1.2:8686/api/v1/release?apiKey=mysecretkey&albumId=123' `
    -Operation 'FetchReleases' `
    -PluginName 'TestPlugin' `
    -Phase 'AlbumSearch:FetchReleases'

# The endpoint should NOT leak the internal IP
Assert-True "FullURL endpoint does not contain 192.168" (-not ($fullUrlDetails.endpoint -match '192\.168'))
# The endpoint should be path-only
Assert-True "FullURL endpoint starts with /" ($fullUrlDetails.endpoint -match '^/')
# apiKey should be redacted
Assert-True "FullURL endpoint has apiKey redacted" ($fullUrlDetails.endpoint -match 'apiKey=\[REDACTED\]')
# albumId should NOT be redacted (not sensitive)
Assert-True "FullURL endpoint preserves albumId" ($fullUrlDetails.endpoint -match 'albumId=123')

# Test: Path-only endpoint should pass through unchanged
$pathOnlyDetails = New-ApiTimeoutDetails `
    -TimeoutType 'commandPoll' `
    -TimeoutSeconds 60 `
    -Endpoint '/api/v1/command/42' `
    -Operation 'AlbumSearch' `
    -PluginName 'TestPlugin2' `
    -Phase 'AlbumSearch:PollCommand'

Assert-Equal "PathOnly endpoint preserved" $pathOnlyDetails.endpoint '/api/v1/command/42'

# Test: token query param should also be redacted
$tokenDetails = New-ApiTimeoutDetails `
    -TimeoutType 'http' `
    -TimeoutSeconds 10 `
    -Endpoint '/api/v1/indexer/test?token=abc123&name=foo' `
    -Operation 'IndexerTest' `
    -PluginName 'TestPlugin3' `
    -Phase 'Search:TestIndexer'

Assert-True "Token param is redacted" ($tokenDetails.endpoint -match 'token=\[REDACTED\]')
Assert-True "Name param is preserved" ($tokenDetails.endpoint -match 'name=foo')

Write-Host "`nTest Group: E2E_IMPORT_FAILED (P1)" -ForegroundColor Yellow

# Test command failure
$importFailedCmd = [PSCustomObject]@{
    Gate = 'ImportList'
    PluginName = 'Brainarr'
    Outcome = 'failed'
    Errors = @('ImportListSync command failed: Connection refused')
    Details = @{
        ErrorCode = 'E2E_IMPORT_FAILED'
        pluginName = 'Brainarr'
        importListId = 42
        importListName = 'Brainarr AI Recommendations'
        operation = 'ImportListSync'
        phase = 'ImportList:PollCommand'
        endpoint = '/api/v1/command/123'
        commandId = 123
        commandStatus = 'failed'
        postSyncVerified = $false
        preSyncImportListFound = $true
    }
}

$jsonImportFailed = ConvertTo-E2ERunManifest -Results @($importFailedCmd) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}
$mImportFailed = $jsonImportFailed | ConvertFrom-Json
$importFailedResult = $mImportFailed.results | Where-Object { $_.gate -eq 'ImportList' } | Select-Object -First 1

Assert-Equal "ImportFailed errorCode is E2E_IMPORT_FAILED" $importFailedResult.errorCode 'E2E_IMPORT_FAILED'
Assert-True "ImportFailed details has no errorCode key (moved to top-level)" (-not ($importFailedResult.details.PSObject.Properties.Name -contains 'errorCode'))
Assert-Equal "ImportFailed details.importListId is 42" $importFailedResult.details.importListId 42
Assert-Equal "ImportFailed details.phase is ImportList:PollCommand" $importFailedResult.details.phase 'ImportList:PollCommand'
Assert-Equal "ImportFailed details.operation is ImportListSync" $importFailedResult.details.operation 'ImportListSync'
Assert-True "ImportFailed details.endpoint starts with /" ($importFailedResult.details.endpoint -match '^/')
Assert-Equal "ImportFailed details.commandStatus is failed" $importFailedResult.details.commandStatus 'failed'
Assert-Equal "ImportFailed details.preSyncImportListFound is true" $importFailedResult.details.preSyncImportListFound $true

# Test post-sync verification with lastSyncError
$postSyncFailed = [PSCustomObject]@{
    Gate = 'ImportList'
    PluginName = 'Brainarr'
    Outcome = 'failed'
    Errors = @('Import list sync error reported: LLM API returned error')
    Details = @{
        ErrorCode = 'E2E_IMPORT_FAILED'
        pluginName = 'Brainarr'
        importListId = 42
        operation = 'ImportListSync'
        phase = 'ImportList:PostSyncVerify'
        endpoint = '/api/v1/importlist/42'
        commandId = 456
        commandStatus = 'completed'
        lastSyncError = 'LLM API returned error'
        postSyncVerified = $true
        preSyncImportListFound = $true
    }
}

$jsonPostSync = ConvertTo-E2ERunManifest -Results @($postSyncFailed) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}
$mPostSync = $jsonPostSync | ConvertFrom-Json
$postSyncResult = $mPostSync.results | Where-Object { $_.details.phase -eq 'ImportList:PostSyncVerify' } | Select-Object -First 1

Assert-Equal "PostSync errorCode is E2E_IMPORT_FAILED" $postSyncResult.errorCode 'E2E_IMPORT_FAILED'
Assert-Equal "PostSync details.postSyncVerified is true" $postSyncResult.details.postSyncVerified $true
Assert-True "PostSync details.lastSyncError exists" ($null -ne $postSyncResult.details.lastSyncError)

Write-Host "`nTest Group: lastSyncError Sanitization (New-ImportFailedDetails)" -ForegroundColor Yellow

# Test lastSyncError sanitization - URLs and secrets should be redacted
$lastSyncDetails = New-ImportFailedDetails `
    -PluginName 'Brainarr' `
    -ImportListId 99 `
    -Phase 'ImportList:PostSyncVerify' `
    -Endpoint '/api/v1/importlist/99' `
    -LastSyncError 'Failed to connect to http://192.168.1.100:1234/api?apiKey=mysecret&token=abc123'

Assert-True "lastSyncError URL is redacted" ($lastSyncDetails.lastSyncError -match '\[REDACTED-URL\]')
Assert-True "lastSyncError does not contain 192.168" (-not ($lastSyncDetails.lastSyncError -match '192\.168'))


Write-Host "`nTest Group: E2E_CONFIG_INVALID (P1)" -ForegroundColor Yellow

# Case 1: Indexer create invalid
$configInvalidCreate = [PSCustomObject]@{
    Gate = 'Configure'
    PluginName = 'Qobuzarr'
    Outcome = 'failed'
    Errors = @('Validation failed: Priority must be between 1 and 50')
    Details = @{
        ErrorCode = 'E2E_CONFIG_INVALID'
        pluginName = 'Qobuzarr'
        componentType = 'indexer'
        operation = 'create'
        endpoint = '/api/v1/indexer'
        phase = 'Configure:Create:Post'
        httpStatus = 400
        validationErrors = @('Priority must be between 1 and 50')
        validationErrorCount = 1
        validationErrorsCapped = $false
        fieldNames = @('priority')
        fieldNameCount = 1
        fieldNamesCapped = $false
    }
}

$jsonConfigInvalid = ConvertTo-E2ERunManifest -Results @($configInvalidCreate) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}
$mConfigInvalid = $jsonConfigInvalid | ConvertFrom-Json
$configInvalidResult = $mConfigInvalid.results | Where-Object { $_.gate -eq 'Configure' } | Select-Object -First 1

Assert-Equal "ConfigInvalid errorCode is E2E_CONFIG_INVALID" $configInvalidResult.errorCode 'E2E_CONFIG_INVALID'
Assert-True "ConfigInvalid details has no errorCode key (moved to top-level)" (-not ($configInvalidResult.details.PSObject.Properties.Name -contains 'errorCode'))
Assert-Equal "ConfigInvalid details.componentType is indexer" $configInvalidResult.details.componentType 'indexer'
Assert-Equal "ConfigInvalid details.operation is create" $configInvalidResult.details.operation 'create'
Assert-True "ConfigInvalid details.endpoint starts with /api/" ($configInvalidResult.details.endpoint -match '^/api/')
Assert-Equal "ConfigInvalid details.phase is Configure:Create:Post" $configInvalidResult.details.phase 'Configure:Create:Post'
Assert-Equal "ConfigInvalid details.httpStatus is 400" $configInvalidResult.details.httpStatus 400
Assert-Equal "ConfigInvalid details.validationErrorCount is 1" $configInvalidResult.details.validationErrorCount 1
Assert-Equal "ConfigInvalid details.validationErrorsCapped is false" $configInvalidResult.details.validationErrorsCapped $false

# Case 2: Validation errors capped (50 items -> 10 in output)
$manyErrors = @(1..50 | ForEach-Object { "Validation error $_" })
$configInvalidCapped = [PSCustomObject]@{
    Gate = 'Configure'
    PluginName = 'Tidalarr'
    Outcome = 'failed'
    Errors = @('Multiple validation failures')
    Details = @{
        ErrorCode = 'E2E_CONFIG_INVALID'
        pluginName = 'Tidalarr'
        componentType = 'downloadClient'
        operation = 'update'
        endpoint = '/api/v1/downloadclient/42'
        phase = 'Configure:Update:Put'
        validationErrors = @($manyErrors | Select-Object -First 10)
        validationErrorCount = 50
        validationErrorsCapped = $true
        fieldNames = @()
        fieldNameCount = 0
        fieldNamesCapped = $false
        componentId = 42
    }
}

$jsonCapped = ConvertTo-E2ERunManifest -Results @($configInvalidCapped) -Context @{
    LidarrUrl = 'http://localhost:1234'
    ContainerName = 'lidarr-e2e-test'
}
$mCapped = $jsonCapped | ConvertFrom-Json
$cappedResult = $mCapped.results | Where-Object { $_.plugin -eq 'Tidalarr' } | Select-Object -First 1

Assert-Equal "CappedErrors errorCode is E2E_CONFIG_INVALID" $cappedResult.errorCode 'E2E_CONFIG_INVALID'
Assert-True "CappedErrors validationErrors count <= 10" ($cappedResult.details.validationErrors.Count -le 10)
Assert-Equal "CappedErrors validationErrorCount is 50" $cappedResult.details.validationErrorCount 50
Assert-Equal "CappedErrors validationErrorsCapped is true" $cappedResult.details.validationErrorsCapped $true
Assert-Equal "CappedErrors componentId is 42" $cappedResult.details.componentId 42

Write-Host "`nTest Group: Endpoint Normalization (New-ConfigInvalidDetails)" -ForegroundColor Yellow

# Case 3: Endpoint normalization - full URL should be stripped to path-only with redaction
$fullUrlDetails = New-ConfigInvalidDetails `
    -PluginName 'TestPlugin' `
    -ComponentType 'indexer' `
    -Operation 'create' `
    -Endpoint 'http://192.168.1.10:8686/api/v1/indexer?apiKey=mysecretkey&priority=25' `
    -Phase 'Configure:Create:Post' `
    -ValidationErrors @('Validation failed')

Assert-True "ConfigInvalid endpoint does not contain 192.168" (-not ($fullUrlDetails.endpoint -match '192\.168'))
Assert-True "ConfigInvalid endpoint starts with /" ($fullUrlDetails.endpoint -match '^/')
Assert-True "ConfigInvalid endpoint has apiKey redacted" ($fullUrlDetails.endpoint -match 'apiKey=\[REDACTED\]')
Assert-True "ConfigInvalid endpoint preserves priority" ($fullUrlDetails.endpoint -match 'priority=25')

# Test validation error sanitization
$urlInErrorDetails = New-ConfigInvalidDetails `
    -PluginName 'TestPlugin2' `
    -ComponentType 'downloadClient' `
    -Operation 'update' `
    -Endpoint '/api/v1/downloadclient/5' `
    -Phase 'Configure:Update:Put' `
    -ValidationErrors @('Failed to connect to http://internal.server:8080/api?token=abc123')

Assert-True "ValidationError URL is redacted" ($urlInErrorDetails.validationErrors[0] -match '\[REDACTED-URL\]')
Assert-True "ValidationError does not contain internal.server" (-not ($urlInErrorDetails.validationErrors[0] -match 'internal\.server'))


Write-Host "`nTest Group: E2E_CONFIG_INVALID Wiring Verification (P1)" -ForegroundColor Yellow

# Verify that e2e-runner.ps1 has the correct wiring in place
$runnerPath = Join-Path $repoRoot 'scripts/e2e-runner.ps1'
$runnerContent = Get-Content -Path $runnerPath -Raw

# Verify Invoke-ConfigureRequest exists and calls New-ConfigInvalidDetails
Assert-True "e2e-runner.ps1 defines Invoke-ConfigureRequest" ($runnerContent -match 'function Invoke-ConfigureRequest')
Assert-True "Invoke-ConfigureRequest calls New-ConfigInvalidDetails" ($runnerContent -match 'Invoke-ConfigureRequest[\s\S]*?New-ConfigInvalidDetails')

# Verify New-ComponentFromEnv uses Invoke-ConfigureRequest
Assert-True "New-ComponentFromEnv calls Invoke-ConfigureRequest" ($runnerContent -match 'function New-ComponentFromEnv[\s\S]*?Invoke-ConfigureRequest[\s\S]*?function Update-ComponentAuthFields')

# Verify Update-ComponentAuthFields uses Invoke-ConfigureRequest
Assert-True "Update-ComponentAuthFields calls Invoke-ConfigureRequest" ($runnerContent -match 'function Update-ComponentAuthFields[\s\S]*?Invoke-ConfigureRequest[\s\S]*?function Test-ConfigureGateForPlugin')

# Verify Test-ConfigureGateForPlugin merges Details on failure
Assert-True "Test-ConfigureGateForPlugin merges createResult.Details" ($runnerContent -match 'createResult\.Details\.Keys')
Assert-True "Test-ConfigureGateForPlugin merges updateResult.Details" ($runnerContent -match 'updateResult\.Details\.Keys')
Assert-True "Test-ConfigureGateForPlugin checks createResult.Success" ($runnerContent -match 'createResult\.Success')
Assert-True "Test-ConfigureGateForPlugin checks updateResult.Failed" ($runnerContent -match 'updateResult\.Failed')

# Verify the wiring is consistent across all three component types
$indexerWiring = ($runnerContent -match 'New-ComponentFromEnv -Type "indexer"[\s\S]*?createResult\.Success')
$downloadClientWiring = ($runnerContent -match 'New-ComponentFromEnv -Type "downloadclient"[\s\S]*?createResult\.Success')
$importListWiring = ($runnerContent -match 'New-ComponentFromEnv -Type "importlist"[\s\S]*?createResult\.Success')

Assert-True "Indexer wiring uses structured result" $indexerWiring
Assert-True "DownloadClient wiring uses structured result" $downloadClientWiring
Assert-True "ImportList wiring uses structured result" $importListWiring

# Test that Invoke-ConfigureRequest returns the expected structure
# by simulating what it would return on failure (without actually calling Lidarr API)
Write-Host "`nTest Group: Invoke-ConfigureRequest Return Contract" -ForegroundColor Yellow

# Simulate the exact structure that Invoke-ConfigureRequest returns on failure
$simulatedFailure = @{
    Success = $false
    Details = New-ConfigInvalidDetails `
        -PluginName 'SimulatedPlugin' `
        -ComponentType 'indexer' `
        -Operation 'create' `
        -Endpoint '/api/v1/indexer' `
        -Phase 'Configure:Create:Post' `
        -HttpStatus 400 `
        -ValidationErrors @('Simulated validation error') `
        -FieldNames @('simulatedField') `
        -SchemaContract 'SimulatedPluginSettings'
    Errors = @('Simulated API error')
}

Assert-True "Simulated failure has Success=false" ($simulatedFailure.Success -eq $false)
Assert-True "Simulated failure has Details.ErrorCode" ($simulatedFailure.Details.ErrorCode -eq 'E2E_CONFIG_INVALID')
Assert-True "Simulated failure has Details.pluginName" ($simulatedFailure.Details.pluginName -eq 'SimulatedPlugin')
Assert-True "Simulated failure has Details.componentType" ($simulatedFailure.Details.componentType -eq 'indexer')
Assert-True "Simulated failure has Details.operation" ($simulatedFailure.Details.operation -eq 'create')
Assert-True "Simulated failure has Details.phase" ($simulatedFailure.Details.phase -eq 'Configure:Create:Post')
Assert-True "Simulated failure has Details.httpStatus" ($simulatedFailure.Details.httpStatus -eq 400)
Assert-True "Simulated failure has Details.schemaContract" ($simulatedFailure.Details.schemaContract -eq 'SimulatedPluginSettings')
Assert-True "Simulated failure has Errors array" ($simulatedFailure.Errors.Count -gt 0)

# Verify merging logic produces expected gate result structure
$gateResult = @{
    Gate = 'Configure'
    PluginName = 'SimulatedPlugin'
    Outcome = 'failed'
    Errors = @()
    Details = @{
        componentIds = @{}
        alreadyConfigured = $false
        created = $false
        updated = $false
    }
}

# Simulate the merging logic from Test-ConfigureGateForPlugin
$gateResult.Errors += "Failed to create indexer for SimulatedPlugin"
$gateResult.Outcome = "failed"
foreach ($key in $simulatedFailure.Details.Keys) {
    $gateResult.Details[$key] = $simulatedFailure.Details[$key]
}

Assert-True "Gate result has ErrorCode after merge" ($gateResult.Details.ErrorCode -eq 'E2E_CONFIG_INVALID')
Assert-True "Gate result has componentType after merge" ($gateResult.Details.componentType -eq 'indexer')
Assert-True "Gate result has operation after merge" ($gateResult.Details.operation -eq 'create')
Assert-True "Gate result has validationErrors after merge" ($gateResult.Details.validationErrors.Count -eq 1)
Assert-True "Gate result preserves original componentIds" ($null -ne $gateResult.Details.componentIds)


Write-Host "`nSummary: $passed passed, $failed failed" -ForegroundColor Cyan
if ($failed -gt 0) {
    throw "$failed assertions failed"
}
