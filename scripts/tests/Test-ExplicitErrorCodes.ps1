$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Explicit ErrorCodes (Queue + Metadata + ZeroAudio)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$jsonModule = Join-Path $repoRoot 'scripts/lib/e2e-json-output.psm1'

if (-not (Test-Path $jsonModule)) {
    throw "Module not found: $jsonModule"
}

Import-Module $jsonModule -Force

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

Write-Host "`nSummary: $passed passed, $failed failed" -ForegroundColor Cyan
if ($failed -gt 0) {
    throw "$failed assertions failed"
}
