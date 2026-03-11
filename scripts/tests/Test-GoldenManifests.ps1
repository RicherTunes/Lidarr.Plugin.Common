$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Golden Manifest Fixtures" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$schemaPath = Join-Path $repoRoot 'docs/reference/e2e-run-manifest.schema.json'
$fixturesDir = Join-Path $PSScriptRoot 'fixtures/golden-manifests'

if (-not (Test-Path $schemaPath)) {
    throw "Schema not found: $schemaPath"
}
if (-not (Test-Path $fixturesDir)) {
    throw "Fixtures dir not found: $fixturesDir"
}

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

function Validate-Fixture {
    param([string]$FileName)

    $path = Join-Path $fixturesDir $FileName
    Assert-True -Name "Fixture exists: $FileName" -Condition (Test-Path $path)

    $json = Get-Content -Path $path -Raw
    $manifest = $json | ConvertFrom-Json -Depth 100

    Assert-Equal -Name "$FileName schemaVersion" -Actual $manifest.schemaVersion -Expected '1.2'
    Assert-Equal -Name "$FileName schemaId" -Actual $manifest.schemaId -Expected 'richer-tunes.lidarr.e2e-run-manifest'

    # Full schema validation (PowerShell 7.3+)
    $schemaOk = Test-Json -Json $json -SchemaFile $schemaPath
    Assert-True -Name "$FileName schema-valid" -Condition $schemaOk

    return $manifest
}

$pass = Validate-Fixture -FileName 'pass.json'
Assert-Equal -Name "pass.json overallSuccess" -Actual $pass.summary.overallSuccess -Expected $true

$authMissing = Validate-Fixture -FileName 'auth-missing.json'
Assert-True -Name "auth-missing.json has E2E_AUTH_MISSING" -Condition ($authMissing.results.errorCode -contains 'E2E_AUTH_MISSING')

$ambiguous = Validate-Fixture -FileName 'component-ambiguous.json'
Assert-True -Name "component-ambiguous.json has E2E_COMPONENT_AMBIGUOUS" -Condition ($ambiguous.results.errorCode -contains 'E2E_COMPONENT_AMBIGUOUS')
Assert-True -Name "component-ambiguous.json has candidateIds" -Condition (($ambiguous.results | Where-Object { $_.errorCode -eq 'E2E_COMPONENT_AMBIGUOUS' }).details.candidateIds.Count -gt 1)

$hostBug = Validate-Fixture -FileName 'host-bug-alc.json'
Assert-Equal -Name "host-bug-alc.json hostBugSuspected.detected" -Actual $hostBug.hostBugSuspected.detected -Expected $true
Assert-Equal -Name "host-bug-alc.json hostBugSuspected.classification" -Actual $hostBug.hostBugSuspected.classification -Expected 'ALC'

$schemaMissing = Validate-Fixture -FileName 'schema-missing-implementation.json'
Assert-True -Name "schema-missing-implementation.json has E2E_SCHEMA_MISSING_IMPLEMENTATION" -Condition ($schemaMissing.results.errorCode -contains 'E2E_SCHEMA_MISSING_IMPLEMENTATION')
$schemaMissingResult = $schemaMissing.results | Where-Object { $_.errorCode -eq 'E2E_SCHEMA_MISSING_IMPLEMENTATION' }
Assert-True -Name "schema-missing-implementation.json has discoveryDiagnosis" -Condition ($null -ne $schemaMissingResult.details.discoveryDiagnosis)
Assert-True -Name "schema-missing-implementation.json discoveryDiagnosis.schemaEndpointReachable" -Condition ($schemaMissingResult.details.discoveryDiagnosis.schemaEndpointReachable -eq $true)

$abstractionsMismatch = Validate-Fixture -FileName 'abstractions-sha-mismatch.json'
Assert-True -Name "abstractions-sha-mismatch.json has E2E_ABSTRACTIONS_SHA_MISMATCH" -Condition ($abstractionsMismatch.results.errorCode -contains 'E2E_ABSTRACTIONS_SHA_MISMATCH')
$abstractionsResult = $abstractionsMismatch.results | Where-Object { $_.errorCode -eq 'E2E_ABSTRACTIONS_SHA_MISMATCH' }
Assert-True -Name "abstractions-sha-mismatch.json has fixInstructions" -Condition ($null -ne $abstractionsResult.details.fixInstructions)
Assert-True -Name "abstractions-sha-mismatch.json has abstractionsShas" -Condition ($null -ne $abstractionsResult.details.abstractionsShas)
Assert-True -Name "abstractions-sha-mismatch.json has mismatchedPlugins" -Condition ($abstractionsResult.details.mismatchedPlugins.Count -gt 0)
# Verify no secrets in errors array (should only contain fix instructions, not credentials)
$hasSecretPattern = $abstractionsResult.errors | Where-Object { $_ -match '(password|token|secret|key=)' }
Assert-True -Name "abstractions-sha-mismatch.json errors contain no secrets" -Condition ($null -eq $hasSecretPattern)

$discoveryDisabled = Validate-Fixture -FileName 'host-plugin-discovery-disabled.json'
Assert-True -Name "host-plugin-discovery-disabled.json has E2E_HOST_PLUGIN_DISCOVERY_DISABLED" -Condition ($discoveryDisabled.results.errorCode -contains 'E2E_HOST_PLUGIN_DISCOVERY_DISABLED')
$discoveryResult = $discoveryDisabled.results | Where-Object { $_.errorCode -eq 'E2E_HOST_PLUGIN_DISCOVERY_DISABLED' }
Assert-True -Name "host-plugin-discovery-disabled.json has discoveryDiagnosis" -Condition ($null -ne $discoveryResult.details.discoveryDiagnosis)
# Key contract: schemas reachable but plugin not loaded, with affirmative host evidence
Assert-True -Name "host-plugin-discovery-disabled.json discoveryDiagnosis.schemaEndpointReachable=true" -Condition ($discoveryResult.details.discoveryDiagnosis.schemaEndpointReachable -eq $true)
Assert-True -Name "host-plugin-discovery-disabled.json discoveryDiagnosis.pluginPackagePresent=true" -Condition ($discoveryResult.details.discoveryDiagnosis.pluginPackagePresent -eq $true)
Assert-True -Name "host-plugin-discovery-disabled.json discoveryDiagnosis.hostPluginDiscoveryEnabled=false" -Condition ($discoveryResult.details.discoveryDiagnosis.hostPluginDiscoveryEnabled -eq $false)
# Affirmative evidence required when hostPluginDiscoveryEnabled is false
Assert-True -Name "host-plugin-discovery-disabled.json has detectionBasis" -Condition ($null -ne $discoveryResult.details.discoveryDiagnosis.detectionBasis)

$noReleases = Validate-Fixture -FileName 'no-releases-attributed.json'
Assert-True -Name "no-releases-attributed.json has E2E_NO_RELEASES_ATTRIBUTED" -Condition ($noReleases.results.errorCode -contains 'E2E_NO_RELEASES_ATTRIBUTED')
$noReleasesResult = $noReleases.results | Where-Object { $_.errorCode -eq 'E2E_NO_RELEASES_ATTRIBUTED' }
# Contract: must include counts for debugging
Assert-True -Name "no-releases-attributed.json has totalReleases" -Condition ($null -ne $noReleasesResult.details.totalReleases)
Assert-True -Name "no-releases-attributed.json has attributedReleases" -Condition ($null -ne $noReleasesResult.details.attributedReleases)
Assert-True -Name "no-releases-attributed.json has expectedIndexerName" -Condition ($null -ne $noReleasesResult.details.expectedIndexerName)
Assert-True -Name "no-releases-attributed.json has expectedIndexerId" -Condition ($null -ne $noReleasesResult.details.expectedIndexerId)
Assert-True -Name "no-releases-attributed.json has foundIndexerNames" -Condition ($noReleasesResult.details.foundIndexerNames.Count -ge 0)
Assert-True -Name "no-releases-attributed.json has foundIndexerNameCount" -Condition ($null -ne $noReleasesResult.details.foundIndexerNameCount)
Assert-True -Name "no-releases-attributed.json has foundIndexerNamesCapped" -Condition ($null -ne $noReleasesResult.details.foundIndexerNamesCapped)
Assert-True -Name "no-releases-attributed.json has nullIndexerReleaseCount" -Condition ($null -ne $noReleasesResult.details.nullIndexerReleaseCount)
Assert-True -Name "no-releases-attributed.json has nullIndexerSamples" -Condition ($null -ne $noReleasesResult.details.nullIndexerSamples)
# Guardrail: foundIndexerNames must be capped to prevent manifest bloat
$maxIndexerNames = 10
if ($noReleasesResult.details.foundIndexerNames.Count -gt $maxIndexerNames) {
    Write-Host "  [FAIL] no-releases-attributed.json foundIndexerNames exceeds cap ($($noReleasesResult.details.foundIndexerNames.Count) > $maxIndexerNames)" -ForegroundColor Red
    $script:failed++
} else {
    Write-Host "  [PASS] no-releases-attributed.json foundIndexerNames within cap (<= $maxIndexerNames)" -ForegroundColor Green
    $script:passed++
}
# Guardrail: nullIndexerSamples must be capped to prevent manifest bloat
$maxNullSamples = 3
if ($noReleasesResult.details.nullIndexerSamples.Count -gt $maxNullSamples) {
    Write-Host "  [FAIL] no-releases-attributed.json nullIndexerSamples exceeds cap ($($noReleasesResult.details.nullIndexerSamples.Count) > $maxNullSamples)" -ForegroundColor Red
    $script:failed++
} else {
    Write-Host "  [PASS] no-releases-attributed.json nullIndexerSamples within cap (<= $maxNullSamples)" -ForegroundColor Green
    $script:passed++
}

$metadataMissing = Validate-Fixture -FileName 'metadata-missing.json'
Assert-True -Name "metadata-missing.json has E2E_METADATA_MISSING" -Condition ($metadataMissing.results.errorCode -contains 'E2E_METADATA_MISSING')
$metadataResult = $metadataMissing.results | Where-Object { $_.errorCode -eq 'E2E_METADATA_MISSING' }
# Contract: must include actionable list of missing tags
Assert-True -Name "metadata-missing.json has missingTags array" -Condition ($metadataResult.details.missingTags.Count -gt 0)
Assert-True -Name "metadata-missing.json has sampleFile" -Condition ($null -ne $metadataResult.details.sampleFile)

$zeroAudioFiles = Validate-Fixture -FileName 'zero-audio-files.json'
Assert-True -Name "zero-audio-files.json has E2E_ZERO_AUDIO_FILES" -Condition ($zeroAudioFiles.results.errorCode -contains 'E2E_ZERO_AUDIO_FILES')
$zeroAudioResult = $zeroAudioFiles.results | Where-Object { $_.errorCode -eq 'E2E_ZERO_AUDIO_FILES' }
# Contract: must include outputPath and totalFilesFound=0
Assert-True -Name "zero-audio-files.json has outputPath" -Condition ($null -ne $zeroAudioResult.details.outputPath)
Assert-Equal -Name "zero-audio-files.json totalFilesFound is 0" -Actual $zeroAudioResult.details.totalFilesFound -Expected 0

$apiTimeout = Validate-Fixture -FileName 'api-timeout.json'
Assert-True -Name "api-timeout.json has E2E_API_TIMEOUT" -Condition ($apiTimeout.results.errorCode -contains 'E2E_API_TIMEOUT')
$apiTimeoutResult = $apiTimeout.results | Where-Object { $_.errorCode -eq 'E2E_API_TIMEOUT' }
# Contract: must include structured timeout details
Assert-True -Name "api-timeout.json has timeoutType" -Condition ($null -ne $apiTimeoutResult.details.timeoutType)
Assert-True -Name "api-timeout.json has timeoutSeconds" -Condition ($null -ne $apiTimeoutResult.details.timeoutSeconds)
Assert-True -Name "api-timeout.json has endpoint" -Condition ($null -ne $apiTimeoutResult.details.endpoint)
Assert-True -Name "api-timeout.json has operation" -Condition ($null -ne $apiTimeoutResult.details.operation)
Assert-True -Name "api-timeout.json has pluginName" -Condition ($null -ne $apiTimeoutResult.details.pluginName)
Assert-True -Name "api-timeout.json has phase" -Condition ($null -ne $apiTimeoutResult.details.phase)
# Verify expected values
Assert-Equal -Name "api-timeout.json timeoutType is commandPoll" -Actual $apiTimeoutResult.details.timeoutType -Expected 'commandPoll'
Assert-Equal -Name "api-timeout.json timeoutSeconds is 120" -Actual $apiTimeoutResult.details.timeoutSeconds -Expected 120

$importFailed = Validate-Fixture -FileName 'import-failed.json'
Assert-True -Name "import-failed.json has E2E_IMPORT_FAILED" -Condition ($importFailed.results.errorCode -contains 'E2E_IMPORT_FAILED')
$importFailedResult = $importFailed.results | Where-Object { $_.errorCode -eq 'E2E_IMPORT_FAILED' }
# Contract: must include structured import failed details
Assert-True -Name "import-failed.json has importListId (int)" -Condition ($null -ne $importFailedResult.details.importListId)
Assert-True -Name "import-failed.json has phase (non-empty)" -Condition (-not [string]::IsNullOrWhiteSpace($importFailedResult.details.phase))
Assert-True -Name "import-failed.json has operation" -Condition ($null -ne $importFailedResult.details.operation)
Assert-True -Name "import-failed.json endpoint starts with /api/" -Condition ($importFailedResult.details.endpoint -match '^/api/')
Assert-True -Name "import-failed.json has pluginName" -Condition ($null -ne $importFailedResult.details.pluginName)
Assert-Equal -Name "import-failed.json operation is ImportListSync" -Actual $importFailedResult.details.operation -Expected 'ImportListSync'
Assert-True -Name "import-failed.json has preSyncImportListFound" -Condition ($null -ne $importFailedResult.details.preSyncImportListFound)


$configInvalid = Validate-Fixture -FileName 'config-invalid.json'
Assert-True -Name "config-invalid.json has E2E_CONFIG_INVALID" -Condition ($configInvalid.results.errorCode -contains 'E2E_CONFIG_INVALID')
$configInvalidResult = $configInvalid.results | Where-Object { $_.errorCode -eq 'E2E_CONFIG_INVALID' }
# Contract: must include structured config invalid details
Assert-True -Name "config-invalid.json has componentType (non-empty)" -Condition (-not [string]::IsNullOrWhiteSpace($configInvalidResult.details.componentType))
Assert-True -Name "config-invalid.json has operation (non-empty)" -Condition (-not [string]::IsNullOrWhiteSpace($configInvalidResult.details.operation))
Assert-True -Name "config-invalid.json endpoint starts with /api/" -Condition ($configInvalidResult.details.endpoint -match '^/api/')
Assert-True -Name "config-invalid.json has pluginName" -Condition ($null -ne $configInvalidResult.details.pluginName)
Assert-True -Name "config-invalid.json validationErrors count <= 10" -Condition ($configInvalidResult.details.validationErrors.Count -le 10)
Assert-True -Name "config-invalid.json validationErrorCount >= validationErrors.Count" -Condition ($configInvalidResult.details.validationErrorCount -ge $configInvalidResult.details.validationErrors.Count)
Assert-True -Name "config-invalid.json validationErrorsCapped boolean exists" -Condition ($null -ne $configInvalidResult.details.validationErrorsCapped)


$dockerUnavailable = Validate-Fixture -FileName 'docker-unavailable.json'
Assert-True -Name "docker-unavailable.json has E2E_DOCKER_UNAVAILABLE" -Condition ($dockerUnavailable.results.errorCode -contains 'E2E_DOCKER_UNAVAILABLE')
$dockerUnavailableResult = $dockerUnavailable.results | Where-Object { $_.errorCode -eq 'E2E_DOCKER_UNAVAILABLE' }
# Contract: must include structured docker failure details
Assert-True -Name "docker-unavailable.json has dockerFailureKind" -Condition (-not [string]::IsNullOrWhiteSpace($dockerUnavailableResult.details.dockerFailureKind))
Assert-True -Name "docker-unavailable.json has dockerPhase" -Condition (-not [string]::IsNullOrWhiteSpace($dockerUnavailableResult.details.dockerPhase))
Assert-True -Name "docker-unavailable.json has suggestion" -Condition (-not [string]::IsNullOrWhiteSpace($dockerUnavailableResult.details.suggestion))
Assert-True -Name "docker-unavailable.json operation starts with docker" -Condition ($dockerUnavailableResult.details.operation -match '^docker\s')

$queueNotFound = Validate-Fixture -FileName 'queue-not-found.json'

$lidarrUnreachable = Validate-Fixture -FileName 'lidarr-unreachable.json'
Assert-True -Name "lidarr-unreachable.json has E2E_LIDARR_UNREACHABLE" -Condition ($lidarrUnreachable.results.errorCode -contains 'E2E_LIDARR_UNREACHABLE')
$lidarrUnreachableResult = $lidarrUnreachable.results | Where-Object { $_.errorCode -eq 'E2E_LIDARR_UNREACHABLE' }
# Contract: must include structured Lidarr preflight failure details
Assert-Equal -Name "lidarr-unreachable.json gate is LidarrApi" -Actual $lidarrUnreachableResult.gate -Expected 'LidarrApi'
Assert-True -Name "lidarr-unreachable.json endpoint starts with /api/" -Condition ($lidarrUnreachableResult.details.endpoint -match '^/api/')
Assert-True -Name "lidarr-unreachable.json has unreachableKind (non-empty)" -Condition (-not [string]::IsNullOrWhiteSpace($lidarrUnreachableResult.details.unreachableKind))
Assert-True -Name "lidarr-unreachable.json has exceptionType (non-empty)" -Condition (-not [string]::IsNullOrWhiteSpace($lidarrUnreachableResult.details.exceptionType))
Assert-True -Name "lidarr-unreachable.json has suggestion (non-empty)" -Condition (-not [string]::IsNullOrWhiteSpace($lidarrUnreachableResult.details.suggestion))
Assert-True -Name "queue-not-found.json has E2E_QUEUE_NOT_FOUND" -Condition ($queueNotFound.results.errorCode -contains 'E2E_QUEUE_NOT_FOUND')
$queueNotFoundResult = $queueNotFound.results | Where-Object { $_.errorCode -eq 'E2E_QUEUE_NOT_FOUND' }
# Contract: must include structured queue correlation details
Assert-True -Name "queue-not-found.json has queueTimeoutSec (int)" -Condition ($null -ne $queueNotFoundResult.details.queueTimeoutSec)
Assert-True -Name "queue-not-found.json has queueCount (int)" -Condition ($null -ne $queueNotFoundResult.details.queueCount)
Assert-True -Name "queue-not-found.json has downloadId (non-empty)" -Condition (-not [string]::IsNullOrWhiteSpace($queueNotFoundResult.details.downloadId))
Assert-True -Name "queue-not-found.json has albumId (int)" -Condition ($null -ne $queueNotFoundResult.details.albumId)
Assert-True -Name "queue-not-found.json has indexerName (non-empty)" -Condition (-not [string]::IsNullOrWhiteSpace($queueNotFoundResult.details.indexerName))

Write-Host ""
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor Red

if ($failed -gt 0) {
    throw "Golden manifest fixture tests failed: $failed"
}
