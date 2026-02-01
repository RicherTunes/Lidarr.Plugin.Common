# e2e-persistence.psm1 - Persistence verification helpers for E2E golden-persist gate
# Handles container restart, state revalidation, and duplicate detection

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Import shared helpers for polling and assertions
$helpersPath = Join-Path $PSScriptRoot "e2e-helpers.psm1"
if (Test-Path $helpersPath) {
    Import-Module $helpersPath -Force
}

<#
.SYNOPSIS
    Restarts a Docker container and waits for Lidarr API to become available.
.PARAMETER ContainerName
    Name of the container to restart.
.PARAMETER LidarrUrl
    Base URL for Lidarr API (e.g., http://localhost:8686).
.PARAMETER Headers
    Hashtable with API headers (X-Api-Key).
.PARAMETER StartupTimeoutSeconds
    Max time to wait for API availability after restart.
.OUTPUTS
    PSCustomObject with Success, RestartDurationMs, and Error properties.
#>
function Restart-LidarrContainer {
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName,

        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [int]$StartupTimeoutSeconds = 120
    )

    $result = [PSCustomObject]@{
        Success = $false
        RestartDurationMs = 0
        Error = $null
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        Write-Host "Restarting container '$ContainerName'..." -ForegroundColor Yellow

        # Stop the container gracefully
        $stopOutput = & docker stop $ContainerName 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to stop container: $stopOutput"
        }

        # Start the container
        $startOutput = & docker start $ContainerName 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start container: $startOutput"
        }

        Write-Host "Container restarted, waiting for API..." -ForegroundColor DarkGray

        # Wait for API availability using polling helper
        $apiResult = Wait-LidarrApiReady -LidarrUrl $LidarrUrl -Headers $Headers -TimeoutSeconds $StartupTimeoutSeconds

        if (-not $apiResult.Success) {
            throw $apiResult.Error
        }

        Write-Host "API ready after restart: v$($apiResult.Version) (${apiResult.ElapsedMs}ms)" -ForegroundColor Green

        $stopwatch.Stop()
        $result.Success = $true
        $result.RestartDurationMs = $stopwatch.ElapsedMilliseconds
    }
    catch {
        $stopwatch.Stop()
        $result.Error = $_.Exception.Message
        $result.RestartDurationMs = $stopwatch.ElapsedMilliseconds
    }

    return $result
}

<#
.SYNOPSIS
    Verifies plugin schemas are still available after container restart.
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER ExpectedImplementations
    Hashtable mapping schema type to expected implementation names.
    Example: @{ indexer = @("QobuzIndexer"); downloadclient = @("QobuzDownloadClient") }
.OUTPUTS
    PSCustomObject with Success, MissingImplementations, and Error properties.
#>
function Test-PluginSchemasAfterRestart {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [Parameter(Mandatory)]
        [hashtable]$ExpectedImplementations
    )

    $result = [PSCustomObject]@{
        Success = $false
        MissingImplementations = @()
        Error = $null
    }

    try {
        $missing = @()

        foreach ($schemaType in $ExpectedImplementations.Keys) {
            $implementations = $ExpectedImplementations[$schemaType]
            if (-not $implementations -or $implementations.Count -eq 0) { continue }

            $uri = "$LidarrUrl/api/v1/$schemaType/schema"
            $schemas = Invoke-RestMethod -Uri $uri -Headers $Headers -TimeoutSec 30 -ErrorAction Stop

            foreach ($impl in $implementations) {
                $found = $schemas | Where-Object { $_.implementation -eq $impl }
                if (-not $found) {
                    $missing += "$schemaType/$impl"
                }
            }
        }

        if ($missing.Count -gt 0) {
            $result.MissingImplementations = $missing
            $result.Error = "Missing implementations after restart: $($missing -join ', ')"
        }
        else {
            $result.Success = $true
        }
    }
    catch {
        $result.Error = $_.Exception.Message
    }

    return $result
}

<#
.SYNOPSIS
    Verifies queue/history state persisted after restart (no duplicate grabs).
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER ExpectedAlbumId
    Album ID that should be in queue/history.
.PARAMETER OriginalQueueCount
    Number of queue items before restart (used for duplicate detection).
.PARAMETER TimeoutSeconds
    Max time to wait for queue to stabilize.
.OUTPUTS
    PSCustomObject with Success, QueueCount, HasDuplicates, and Error properties.
#>
function Test-QueueStatePersisted {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [int]$ExpectedAlbumId = 0,

        [int]$OriginalQueueCount = 0,

        [int]$TimeoutSeconds = 30
    )

    $result = [PSCustomObject]@{
        Success = $false
        QueueCount = 0
        HasDuplicates = $false
        Error = $null
    }

    try {
        # Use polling helper to wait for queue to be accessible
        $queueResult = Wait-LidarrQueueCondition `
            -LidarrUrl $LidarrUrl `
            -Headers $Headers `
            -Condition { param($records) $true } `
            -TimeoutSeconds $TimeoutSeconds `
            -IntervalMs 2000 `
            -Description "queue accessible"

        if (-not $queueResult.Success) {
            $result.Error = $queueResult.Error
            return $result
        }

        $records = $queueResult.Records
        $result.QueueCount = $records.Count

        # Check for duplicates (same album appearing multiple times)
        if ($ExpectedAlbumId -gt 0) {
            $albumRecords = @($records | Where-Object { $_.albumId -eq $ExpectedAlbumId })
            if ($albumRecords.Count -gt 1) {
                $result.HasDuplicates = $true
                $result.Error = "Duplicate queue entries detected for album $ExpectedAlbumId (count: $($albumRecords.Count))"
                return $result
            }
        }

        # Check queue count hasn't grown unexpectedly (indicating duplicate grabs)
        if ($OriginalQueueCount -gt 0 -and $result.QueueCount -gt $OriginalQueueCount) {
            $result.HasDuplicates = $true
            $result.Error = "Queue count increased after restart (original: $OriginalQueueCount, now: $($result.QueueCount))"
            return $result
        }

        $result.Success = $true
    }
    catch {
        $result.Error = $_.Exception.Message
    }

    return $result
}

<#
.SYNOPSIS
    Verifies history contains expected record (download was tracked).
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER AlbumId
    Album ID to look for in history.
.PARAMETER MinRecordCount
    Minimum number of history records expected.
.PARAMETER TimeoutSeconds
    Max time to wait for history condition.
.OUTPUTS
    PSCustomObject with Success, HistoryCount, FoundAlbum, and Error properties.
#>
function Test-HistoryContainsAlbum {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [int]$AlbumId = 0,

        [int]$MinRecordCount = 0,

        [int]$TimeoutSeconds = 30
    )

    $result = [PSCustomObject]@{
        Success = $false
        HistoryCount = 0
        FoundAlbum = $false
        Error = $null
    }

    try {
        # Build condition based on parameters
        $condition = {
            param($records)
            if ($AlbumId -gt 0) {
                $albumRecords = @($records | Where-Object { $_.albumId -eq $AlbumId })
                return $albumRecords.Count -gt 0
            }
            return $true
        }

        # Use polling helper to wait for history condition
        $historyResult = Wait-LidarrHistoryCondition `
            -LidarrUrl $LidarrUrl `
            -Headers $Headers `
            -Condition $condition `
            -TimeoutSeconds $TimeoutSeconds `
            -IntervalMs 2000 `
            -Description "history contains album $AlbumId"

        if (-not $historyResult.Success -and $AlbumId -gt 0) {
            # If we were looking for a specific album and didn't find it, that's the error
            $result.Error = "Album $AlbumId not found in history within ${TimeoutSeconds}s"
            return $result
        }

        $records = $historyResult.Records
        $result.HistoryCount = $records.Count

        if ($AlbumId -gt 0) {
            $albumRecords = @($records | Where-Object { $_.albumId -eq $AlbumId })
            $result.FoundAlbum = $albumRecords.Count -gt 0
        }
        else {
            $result.FoundAlbum = $true
        }

        if ($MinRecordCount -gt 0 -and $result.HistoryCount -lt $MinRecordCount) {
            $result.Error = "History count below minimum (expected: >= $MinRecordCount, actual: $($result.HistoryCount))"
            return $result
        }

        $result.Success = $true
    }
    catch {
        $result.Error = $_.Exception.Message
    }

    return $result
}

<#
.SYNOPSIS
    Verifies telemetry signal is still emitted after restart.
.PARAMETER ContainerName
    Name of the container to check logs.
.PARAMETER TelemetryPatterns
    Array of regex patterns to search for in logs.
.PARAMETER TimeoutSeconds
    Max time to wait for telemetry entries.
.OUTPUTS
    PSCustomObject with Success, FoundPatterns, MatchingLines, and Error properties.
#>
function Test-TelemetryAfterRestart {
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName,

        [string[]]$TelemetryPatterns = @(
            '\[LPC_TELEMETRY\]\s*\{[^}]*"event"\s*:\s*"telemetry_emitted"[^}]*\}',
            'Download completed:.*track=.*bytes=.*elapsed=.*rate='
        ),

        [int]$TimeoutSeconds = 30
    )

    $result = [PSCustomObject]@{
        Success = $false
        FoundPatterns = @()
        MatchingLines = @()
        Error = $null
    }

    try {
        # Try each pattern with the Wait-LogPattern helper
        foreach ($pattern in $TelemetryPatterns) {
            $logResult = Wait-LogPattern `
                -ContainerName $ContainerName `
                -Pattern $pattern `
                -TimeoutSeconds $TimeoutSeconds `
                -TailLines 500

            if ($logResult.Success) {
                $result.FoundPatterns += $pattern
                $result.MatchingLines += $logResult.MatchingLines
            }
        }

        if ($result.FoundPatterns.Count -gt 0) {
            $result.Success = $true
        }
        else {
            $result.Error = "No telemetry patterns found within ${TimeoutSeconds}s"
        }
    }
    catch {
        $result.Error = $_.Exception.Message
    }

    return $result
}

<#
.SYNOPSIS
    Runs the complete golden-persist gate: restart + revalidate all state.
.PARAMETER ContainerName
    Name of the container.
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER ExpectedImplementations
    Hashtable mapping schema type to expected implementation names.
.PARAMETER AlbumId
    Album ID to verify in queue/history.
.PARAMETER OriginalQueueCount
    Queue count before restart (for duplicate detection).
.PARAMETER StartupTimeoutSeconds
    Max time to wait for API after restart.
.PARAMETER VerifyTelemetry
    If true, also verify telemetry signals after restart.
.OUTPUTS
    PSCustomObject with Success, Steps (array of step results), and Error properties.
#>
function Invoke-GoldenPersistGate {
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName,

        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [Parameter(Mandatory)]
        [hashtable]$ExpectedImplementations,

        [int]$AlbumId = 0,

        [int]$OriginalQueueCount = 0,

        [int]$StartupTimeoutSeconds = 120,

        [switch]$VerifyTelemetry
    )

    $result = [PSCustomObject]@{
        Success = $false
        Steps = @()
        Error = $null
    }

    # Step 1: Restart container
    Write-Host "`n=== Golden-Persist Gate: Restart Container ===" -ForegroundColor Cyan
    $restartResult = Restart-LidarrContainer -ContainerName $ContainerName -LidarrUrl $LidarrUrl -Headers $Headers -StartupTimeoutSeconds $StartupTimeoutSeconds
    $result.Steps += [PSCustomObject]@{ Step = "Restart"; Success = $restartResult.Success; Details = $restartResult }

    if (-not $restartResult.Success) {
        $result.Error = "Container restart failed: $($restartResult.Error)"
        return $result
    }
    Write-Host "Container restart completed in $($restartResult.RestartDurationMs)ms" -ForegroundColor Green

    # Step 2: Verify plugin schemas
    Write-Host "`n=== Golden-Persist Gate: Verify Plugin Schemas ===" -ForegroundColor Cyan
    $schemaResult = Test-PluginSchemasAfterRestart -LidarrUrl $LidarrUrl -Headers $Headers -ExpectedImplementations $ExpectedImplementations
    $result.Steps += [PSCustomObject]@{ Step = "Schemas"; Success = $schemaResult.Success; Details = $schemaResult }

    if (-not $schemaResult.Success) {
        $result.Error = "Schema verification failed: $($schemaResult.Error)"
        return $result
    }
    Write-Host "All plugin schemas verified after restart" -ForegroundColor Green

    # Step 3: Verify queue state (no duplicates)
    Write-Host "`n=== Golden-Persist Gate: Verify Queue State ===" -ForegroundColor Cyan
    $queueResult = Test-QueueStatePersisted -LidarrUrl $LidarrUrl -Headers $Headers -ExpectedAlbumId $AlbumId -OriginalQueueCount $OriginalQueueCount
    $result.Steps += [PSCustomObject]@{ Step = "Queue"; Success = $queueResult.Success; Details = $queueResult }

    if (-not $queueResult.Success) {
        $result.Error = "Queue state verification failed: $($queueResult.Error)"
        return $result
    }
    Write-Host "Queue state verified (count: $($queueResult.QueueCount), no duplicates)" -ForegroundColor Green

    # Step 4: Verify history
    Write-Host "`n=== Golden-Persist Gate: Verify History ===" -ForegroundColor Cyan
    $historyResult = Test-HistoryContainsAlbum -LidarrUrl $LidarrUrl -Headers $Headers -AlbumId $AlbumId
    $result.Steps += [PSCustomObject]@{ Step = "History"; Success = $historyResult.Success; Details = $historyResult }

    if (-not $historyResult.Success) {
        $result.Error = "History verification failed: $($historyResult.Error)"
        return $result
    }
    Write-Host "History verified (count: $($historyResult.HistoryCount))" -ForegroundColor Green

    # Step 5: Verify telemetry (optional)
    if ($VerifyTelemetry) {
        Write-Host "`n=== Golden-Persist Gate: Verify Telemetry ===" -ForegroundColor Cyan
        $telemetryResult = Test-TelemetryAfterRestart -ContainerName $ContainerName
        $result.Steps += [PSCustomObject]@{ Step = "Telemetry"; Success = $telemetryResult.Success; Details = $telemetryResult }

        if (-not $telemetryResult.Success) {
            $result.Error = "Telemetry verification failed: $($telemetryResult.Error)"
            return $result
        }
        Write-Host "Telemetry signals verified after restart" -ForegroundColor Green
    }

    $result.Success = $true
    Write-Host "`n Golden-Persist Gate: All checks passed" -ForegroundColor Green

    return $result
}

Export-ModuleMember -Function @(
    'Restart-LidarrContainer',
    'Test-PluginSchemasAfterRestart',
    'Test-QueueStatePersisted',
    'Test-HistoryContainsAlbum',
    'Test-TelemetryAfterRestart',
    'Invoke-GoldenPersistGate'
)
