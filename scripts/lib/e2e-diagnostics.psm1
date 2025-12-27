# E2E Diagnostics Bundle for Plugin Testing
# Collects logs, config, and state on failure for AI-assisted triage

function New-DiagnosticsBundle {
    <#
    .SYNOPSIS
        Creates a diagnostics bundle on E2E test failure.

    .DESCRIPTION
        Collects:
        - Lidarr logs (last 500 lines)
        - Plugin logs (if available)
        - Current indexer/download client configuration
        - Queue state
        - System status
        - Run manifest with gate results

    .PARAMETER OutputPath
        Directory to write the diagnostics bundle.

    .PARAMETER ContainerName
        Docker container name to collect logs from.

    .PARAMETER LidarrApiUrl
        Lidarr API URL.

    .PARAMETER LidarrApiKey
        Lidarr API key.

    .PARAMETER GateResults
        Array of gate result objects from the test run.

    .OUTPUTS
        Path to the created diagnostics bundle zip file.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [string]$ContainerName,

        [Parameter(Mandatory)]
        [string]$LidarrApiUrl,

        [Parameter(Mandatory)]
        [string]$LidarrApiKey,

        [Parameter(Mandatory)]
        [array]$GateResults
    )

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $bundleDir = Join-Path $OutputPath "diagnostics-$timestamp"
    New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null

    Write-Host "Creating diagnostics bundle at $bundleDir..." -ForegroundColor Yellow

    # 1. Collect container logs
    try {
        $logsPath = Join-Path $bundleDir "container-logs.txt"
        docker logs --tail 500 $ContainerName 2>&1 | Out-File -FilePath $logsPath -Encoding UTF8
        Write-Host "  - Container logs collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect container logs: $_" -ForegroundColor Red
    }

    # 2. Collect Lidarr API state
    $headers = @{
        'X-Api-Key' = $LidarrApiKey
        'Content-Type' = 'application/json'
    }
    $apiUrl = $LidarrApiUrl.TrimEnd('/')

    # System status
    try {
        $status = Invoke-RestMethod -Uri "$apiUrl/api/v1/system/status" -Headers $headers -TimeoutSec 10
        $status | ConvertTo-Json -Depth 5 | Out-File -FilePath (Join-Path $bundleDir "system-status.json") -Encoding UTF8
        Write-Host "  - System status collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect system status: $_" -ForegroundColor Red
    }

    # Indexer schemas
    try {
        $indexerSchemas = Invoke-RestMethod -Uri "$apiUrl/api/v1/indexer/schema" -Headers $headers -TimeoutSec 10
        $indexerSchemas | ConvertTo-Json -Depth 5 | Out-File -FilePath (Join-Path $bundleDir "indexer-schemas.json") -Encoding UTF8
        Write-Host "  - Indexer schemas collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect indexer schemas: $_" -ForegroundColor Red
    }

    # Download client schemas
    try {
        $clientSchemas = Invoke-RestMethod -Uri "$apiUrl/api/v1/downloadclient/schema" -Headers $headers -TimeoutSec 10
        $clientSchemas | ConvertTo-Json -Depth 5 | Out-File -FilePath (Join-Path $bundleDir "downloadclient-schemas.json") -Encoding UTF8
        Write-Host "  - Download client schemas collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect download client schemas: $_" -ForegroundColor Red
    }

    # Configured indexers
    try {
        $indexers = Invoke-RestMethod -Uri "$apiUrl/api/v1/indexer" -Headers $headers -TimeoutSec 10
        # Redact sensitive fields
        $indexers | ForEach-Object {
            $_.fields | Where-Object { $_.name -match 'password|secret|token|key' } | ForEach-Object {
                $_.value = '[REDACTED]'
            }
        }
        $indexers | ConvertTo-Json -Depth 5 | Out-File -FilePath (Join-Path $bundleDir "configured-indexers.json") -Encoding UTF8
        Write-Host "  - Configured indexers collected (secrets redacted)" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect configured indexers: $_" -ForegroundColor Red
    }

    # Configured download clients
    try {
        $clients = Invoke-RestMethod -Uri "$apiUrl/api/v1/downloadclient" -Headers $headers -TimeoutSec 10
        # Redact sensitive fields
        $clients | ForEach-Object {
            $_.fields | Where-Object { $_.name -match 'password|secret|token|key' } | ForEach-Object {
                $_.value = '[REDACTED]'
            }
        }
        $clients | ConvertTo-Json -Depth 5 | Out-File -FilePath (Join-Path $bundleDir "configured-downloadclients.json") -Encoding UTF8
        Write-Host "  - Configured download clients collected (secrets redacted)" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect configured download clients: $_" -ForegroundColor Red
    }

    # Queue state
    try {
        $queue = Invoke-RestMethod -Uri "$apiUrl/api/v1/queue" -Headers $headers -TimeoutSec 10
        $queue | ConvertTo-Json -Depth 5 | Out-File -FilePath (Join-Path $bundleDir "queue-state.json") -Encoding UTF8
        Write-Host "  - Queue state collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect queue state: $_" -ForegroundColor Red
    }

    # 3. Write run manifest
    $manifest = [PSCustomObject]@{
        timestamp = (Get-Date).ToString('o')
        container = $ContainerName
        lidarrUrl = $LidarrApiUrl
        gates = $GateResults
        overallSuccess = ($GateResults | Where-Object { -not $_.Success }).Count -eq 0
        failedGates = $GateResults | Where-Object { -not $_.Success } | Select-Object -ExpandProperty Gate
    }
    $manifest | ConvertTo-Json -Depth 5 | Out-File -FilePath (Join-Path $bundleDir "run-manifest.json") -Encoding UTF8
    Write-Host "  - Run manifest created" -ForegroundColor Green

    # 4. Create zip bundle
    $zipPath = "$bundleDir.zip"
    Compress-Archive -Path "$bundleDir\*" -DestinationPath $zipPath -Force
    Remove-Item -Path $bundleDir -Recurse -Force

    Write-Host ""
    Write-Host "Diagnostics bundle created: $zipPath" -ForegroundColor Cyan
    Write-Host "Share this bundle for AI-assisted triage." -ForegroundColor Yellow

    return $zipPath
}

function Get-FailureSummary {
    <#
    .SYNOPSIS
        Generates a human-readable failure summary from gate results.
    #>
    param(
        [Parameter(Mandatory)]
        [array]$GateResults
    )

    $summary = @()
    $summary += "E2E Test Failure Summary"
    $summary += "========================"
    $summary += ""

    foreach ($gate in $GateResults) {
        $status = if ($gate.Success) { "[PASS]" } else { "[FAIL]" }
        $summary += "$status $($gate.Gate) Gate"

        if ($gate.Errors -and $gate.Errors.Count -gt 0) {
            foreach ($error in $gate.Errors) {
                $summary += "       - $error"
            }
        }
    }

    return $summary -join "`n"
}

Export-ModuleMember -Function New-DiagnosticsBundle, Get-FailureSummary
