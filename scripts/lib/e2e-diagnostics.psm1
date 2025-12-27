# E2E Diagnostics Bundle for Plugin Testing
# Collects logs, config, and state on failure for AI-assisted triage

# Sensitive field patterns to redact (comprehensive list)
$script:SensitivePatterns = @(
    'password',
    'secret',
    'token',
    'key',
    'apikey',
    'api_key',
    'bearer',
    'credential',
    'auth',
    'accesstoken',
    'access_token',
    'refreshtoken',
    'refresh_token',
    'clientsecret',
    'client_secret',
    'privatekey',
    'private_key'
)

function Invoke-SecretRedaction {
    <#
    .SYNOPSIS
        Redacts sensitive fields from objects before serialization.

    .DESCRIPTION
        Uses a denylist of known sensitive field patterns.
        Recursively processes nested objects and arrays.
        Safe for JSON serialization after redaction.
    #>
    param(
        [Parameter(Mandatory)]
        $Object
    )

    if ($null -eq $Object) { return $null }

    # Handle arrays
    if ($Object -is [array]) {
        return @($Object | ForEach-Object { Invoke-SecretRedaction -Object $_ })
    }

    # Handle PSCustomObject or hashtable-like objects
    if ($Object -is [PSCustomObject] -or $Object -is [hashtable]) {
        $result = [PSCustomObject]@{}

        $properties = if ($Object -is [hashtable]) { $Object.Keys } else { $Object.PSObject.Properties.Name }

        foreach ($prop in $properties) {
            $value = if ($Object -is [hashtable]) { $Object[$prop] } else { $Object.$prop }

            # Check if property name matches sensitive patterns
            $isSensitive = $false
            foreach ($pattern in $script:SensitivePatterns) {
                if ($prop -match $pattern) {
                    $isSensitive = $true
                    break
                }
            }

            if ($isSensitive -and $null -ne $value -and $value -ne '') {
                $result | Add-Member -NotePropertyName $prop -NotePropertyValue '[REDACTED]'
            }
            elseif ($value -is [array] -or $value -is [PSCustomObject] -or $value -is [hashtable]) {
                $result | Add-Member -NotePropertyName $prop -NotePropertyValue (Invoke-SecretRedaction -Object $value)
            }
            else {
                $result | Add-Member -NotePropertyName $prop -NotePropertyValue $value
            }
        }

        # Special handling for Lidarr 'fields' array pattern
        if ($result.PSObject.Properties['fields'] -and $result.fields -is [array]) {
            $result.fields = @($result.fields | ForEach-Object {
                $field = $_
                $isSensitive = $false
                if ($field.name) {
                    foreach ($pattern in $script:SensitivePatterns) {
                        if ($field.name -match $pattern) {
                            $isSensitive = $true
                            break
                        }
                    }
                }
                if ($isSensitive -and $field.value) {
                    $field = $field.PSObject.Copy()
                    $field.value = '[REDACTED]'
                }
                $field
            })
        }

        return $result
    }

    # Return primitives as-is
    return $Object
}

function Test-SecretRedaction {
    <#
    .SYNOPSIS
        Verifies the redactor removes common sensitive patterns.

    .DESCRIPTION
        Self-test function to validate redaction logic.
        Call this to verify the redactor is working correctly.

    .OUTPUTS
        $true if all patterns are properly redacted, throws otherwise.
    #>
    $testCases = @(
        @{ Input = @{ password = 'secret123' }; Field = 'password' }
        @{ Input = @{ apiKey = 'key123' }; Field = 'apiKey' }
        @{ Input = @{ accessToken = 'token123' }; Field = 'accessToken' }
        @{ Input = @{ client_secret = 'secret' }; Field = 'client_secret' }
        @{ Input = @{ bearerToken = 'bearer123' }; Field = 'bearerToken' }
        @{ Input = @{ fields = @(@{ name = 'password'; value = 'secret' }) }; Field = 'fields[0].value' }
    )

    foreach ($case in $testCases) {
        $result = Invoke-SecretRedaction -Object ([PSCustomObject]$case.Input)

        if ($case.Field -eq 'fields[0].value') {
            if ($result.fields[0].value -ne '[REDACTED]') {
                throw "Redaction failed for nested field pattern: $($case.Field)"
            }
        }
        else {
            $fieldName = $case.Field
            if ($result.$fieldName -ne '[REDACTED]') {
                throw "Redaction failed for pattern: $fieldName"
            }
        }
    }

    return $true
}

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
        $indexers = Invoke-SecretRedaction -Object $indexers
        $indexers | ConvertTo-Json -Depth 5 | Out-File -FilePath (Join-Path $bundleDir "configured-indexers.json") -Encoding UTF8
        Write-Host "  - Configured indexers collected (secrets redacted)" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect configured indexers: $_" -ForegroundColor Red
    }

    # Configured download clients
    try {
        $clients = Invoke-RestMethod -Uri "$apiUrl/api/v1/downloadclient" -Headers $headers -TimeoutSec 10
        $clients = Invoke-SecretRedaction -Object $clients
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

Export-ModuleMember -Function New-DiagnosticsBundle, Get-FailureSummary, Invoke-SecretRedaction, Test-SecretRedaction
