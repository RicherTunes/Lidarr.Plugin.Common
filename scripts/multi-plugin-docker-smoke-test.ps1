#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Multi-plugin Docker smoke test for Lidarr plugins.

.DESCRIPTION
    Starts a Lidarr Docker container, mounts one or more plugin packages (zip files),
    waits for Lidarr to become available, then verifies plugin discovery via:
      - /api/v1/indexer/schema
      - /api/v1/downloadclient/schema

    This is a "basic gate" intended to catch runtime load failures and type-identity
    mismatches when multiple plugins co-exist in the same Lidarr instance.

.PARAMETER LidarrTag
    Lidarr Docker image tag to run (plugins branch). Default: pr-plugins-2.14.2.4786

.PARAMETER ContainerName
    Docker container name. Default: lidarr-multi-plugin-smoke

.PARAMETER Port
    Host port to bind Lidarr to. Default: 8689

.PARAMETER StartupTimeoutSeconds
    Max time to wait for Lidarr startup. Default: 120

.PARAMETER SchemaTimeoutSeconds
    Max time to wait for schemas to include plugin implementations. Default: 60

.PARAMETER RunMediumGate
    Optional "medium gate" that attempts to configure and test supported indexers
    via Lidarr's API (requires credentials via environment variables; see notes below).

.PARAMETER MediumGateTimeoutSeconds
    Max time to wait for medium-gate API calls to complete. Default: 60

.PARAMETER RunDownloadClientGate
    Optional gate that configures and tests supported download clients via Lidarr's API.
    This is credential-gated (depends on the same env vars used by the medium gate).

.PARAMETER RunSearchGate
    Optional "search gate" that performs a real Lidarr album search and asserts
    that the release list is non-empty (and, when both indexers are configured,
    that results include releases attributed to each indexer name).

.PARAMETER SearchTimeoutSeconds
    Max time to wait for Lidarr search commands and results. Default: 180

.PARAMETER SearchArtistTerm
    Artist term used to seed Lidarr with a known artist via /api/v1/artist/lookup.
    Default: "Miles Davis"

.PARAMETER SearchAlbumTitle
    Album title expected to exist under the seeded artist and used for AlbumSearch.
    Default: "Kind of Blue"

.PARAMETER RequireAllConfiguredIndexersInSearch
    When set, the search gate fails unless releases include entries attributed to every
    indexer configured by the medium gate. Default: false (only asserts non-empty releases).

.PARAMETER PluginZip
    One or more plugin zips in the form: name=path
    Example: qobuzarr=D:\repo\Qobuzarr-latest.zip

.PARAMETER PluginsOwner
    Owner folder under /config/plugins. Default: RicherTunes

.PARAMETER KeepRunning
    Do not stop/remove the container after the test.

.NOTES
    Medium gate environment variables:
      Qobuzarr (implementation: QobuzIndexer)
        - QOBUZARR_AUTH_METHOD: Email|Token (optional; auto-detect if omitted)
        - QOBUZARR_EMAIL + QOBUZARR_PASSWORD (Email mode)
        - QOBUZARR_USER_ID + QOBUZARR_AUTH_TOKEN (Token mode)
        - QOBUZARR_APP_ID + QOBUZARR_APP_SECRET (recommended)
        - QOBUZARR_COUNTRY_CODE (optional; default US)

      Tidalarr (implementation: TidalLidarrIndexer)
        - TIDALARR_REDIRECT_URL (required)
        - TIDALARR_MARKET (optional; default US)

.EXAMPLE
    .\scripts\multi-plugin-docker-smoke-test.ps1 `
      -PluginZip "qobuzarr=D:\Alex\github\qobuzarr\Qobuzarr-latest.zip" `
      -PluginZip "tidalarr=D:\Alex\github\tidalarr\Tidalarr-latest.zip" `
      -RunMediumGate
#>

[CmdletBinding()]
param(
    [string]$LidarrTag = "pr-plugins-2.14.2.4786",
    [string]$ContainerName = "lidarr-multi-plugin-smoke",
    [int]$Port = 8689,
    [int]$StartupTimeoutSeconds = 120,
    [int]$SchemaTimeoutSeconds = 60,
    [switch]$RunMediumGate,
    [int]$MediumGateTimeoutSeconds = 60,
    [switch]$RunDownloadClientGate,
    [switch]$RunSearchGate,
    [int]$SearchTimeoutSeconds = 180,
    [string]$SearchArtistTerm = "Miles Davis",
    [string]$SearchAlbumTitle = "Kind of Blue",
    [switch]$RequireAllConfiguredIndexersInSearch,
    [string[]]$PluginZip = @(),
    [string]$PluginsOwner = "RicherTunes",
    [switch]$KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if ($RunSearchGate) {
    $RunMediumGate = $true
}
if ($RunDownloadClientGate) {
    $RunMediumGate = $true
}

$expectations = @{
    "qobuzarr" = @{
        Indexers = @("QobuzIndexer")
        DownloadClients = @("QobuzDownloadClient")
    }
    "tidalarr" = @{
        Indexers = @("TidalLidarrIndexer")
        DownloadClients = @("TidalLidarrDownloadClient")
    }
}

function Get-PluginFolderName {
    param([Parameter(Mandatory = $true)][string]$Name)

    $n = $Name.Trim()
    if ([string]::IsNullOrWhiteSpace($n)) { return $n }
    if ($n.Length -eq 1) { return $n.ToUpperInvariant() }
    return $n.Substring(0, 1).ToUpperInvariant() + $n.Substring(1)
}

function Ensure-DockerAvailable {
    $null = & docker ps 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Docker is not available. Start Docker Desktop (Linux engine) and re-run."
    }
}

function Get-NonEmptyEnvValue {
    param([Parameter(Mandatory = $true)][string]$Name)

    $v = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($v)) { return $null }
    return $v.Trim()
}

function Copy-JsonObject {
    param([Parameter(Mandatory = $true)]$Object)
    return ($Object | ConvertTo-Json -Depth 50) | ConvertFrom-Json
}

function UrlEncode {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [uri]::EscapeDataString($Value)
}

function Set-FieldValue {
    param(
        [Parameter(Mandatory = $true)]$Schema,
        [Parameter(Mandatory = $true)][string[]]$CandidateNames,
        [Parameter(Mandatory = $true)]$Value
    )

    if (-not $Schema.fields) {
        throw "Schema for '$($Schema.implementation)' does not include a 'fields' array."
    }

    $found = $null
    foreach ($name in $CandidateNames) {
        $found = $Schema.fields | Where-Object {
            $_.name -and $_.name.ToString().Equals($name, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1

        if ($found) { break }
    }

    if (-not $found) {
        $available = ($Schema.fields | ForEach-Object { $_.name } | Sort-Object -Unique) -join ", "
        throw "Could not find field '$($CandidateNames -join '|')' for '$($Schema.implementation)'. Available: $available"
    }

    if ($found.PSObject.Properties.Match("value").Count -gt 0) {
        $found.value = $Value
    }
    else {
        $found | Add-Member -NotePropertyName "value" -NotePropertyValue $Value -Force
    }
}

function Invoke-LidarrApiJson {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("GET", "POST", "PUT", "DELETE")][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter()][object]$Body,
        [Parameter()][int]$TimeoutSeconds = 60
    )

    $bodyJson = $null
    if ($null -ne $Body) {
        $bodyJson = $Body | ConvertTo-Json -Depth 50 -Compress
    }

    try {
        if ($null -ne $bodyJson) {
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -Body $bodyJson -ContentType "application/json" -TimeoutSec $TimeoutSeconds -ErrorAction Stop
        }

        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -TimeoutSec $TimeoutSeconds -ErrorAction Stop
    }
    catch {
        $message = $_.Exception.Message
        $statusCode = $null
        $responseBody = $null

        try {
            $resp = $_.Exception.Response
            if ($resp) {
                $statusCode = [int]$resp.StatusCode
                $stream = $resp.GetResponseStream()
                if ($stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $responseBody = $reader.ReadToEnd()
                }
            }
        }
        catch {
            # Best-effort only
        }

        $detail = $message
        if ($statusCode) { $detail = "${detail} (HTTP $statusCode)" }
        if (-not [string]::IsNullOrWhiteSpace($responseBody)) {
            $snippet = $responseBody
            $snippet = $snippet -replace '(?i)"(apiKey|password|authToken|appSecret|token|secret|session|credential)"\s*:\s*"[^"]*"', '"$1":"***"'
            $snippet = $snippet -replace '[\x00-\x08\x0B\x0C\x0E-\x1F]', ''
            if ($snippet.Length -gt 800) { $snippet = $snippet.Substring(0, 800) + "..." }
            $detail = "${detail}`nResponse:`n$snippet"
        }

        throw $detail
    }
}

function Wait-LidarrCommandCompleted {
    param(
        [Parameter(Mandatory = $true)][int]$CommandId,
        [Parameter(Mandatory = $true)][string]$LidarrUrl,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $start = Get-Date
    while (((Get-Date) - $start).TotalSeconds -lt $TimeoutSeconds) {
        $cmd = Invoke-LidarrApiJson -Method "GET" -Uri "$LidarrUrl/api/v1/command/$CommandId" -Headers $Headers -TimeoutSeconds 30
        $status = $cmd.status
        if ($status -eq "completed") { return $cmd }
        if ($status -eq "failed" -or $status -eq "aborted" -or $status -eq "cancelled") {
            $msg = $cmd.message
            if ([string]::IsNullOrWhiteSpace($msg)) { $msg = "Lidarr command ended with status '$status'." }
            throw $msg
        }

        Start-Sleep -Seconds 3
    }

    throw "Timeout waiting for Lidarr command $CommandId to complete within ${TimeoutSeconds}s."
}

function Cleanup {
    if (-not $KeepRunning) {
        & docker rm -f $ContainerName 2>$null | Out-Null
    }
}

trap {
    Cleanup
    throw
}

try {
    Ensure-DockerAvailable

    if ($PluginZip.Count -eq 0) {
        throw "No plugins specified. Provide at least one -PluginZip name=path argument."
    }

    Write-Host "=== Multi-Plugin Docker Smoke Test ===" -ForegroundColor Cyan
    Write-Host "Lidarr tag: $LidarrTag"
    Write-Host "Container: $ContainerName"
    Write-Host "Port: $Port"

    $workRoot = Join-Path $repoRoot ".docker-multi-smoke-test/$ContainerName"
    $pluginsRoot = Join-Path $workRoot "plugins"
    $configRoot = Join-Path $workRoot "config"
    $musicRoot = Join-Path $workRoot "music"
    $downloadsRoot = Join-Path $workRoot "downloads"

    if (Test-Path $workRoot) {
        Remove-Item -Recurse -Force $workRoot
    }
    New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $configRoot | Out-Null
    if ($RunSearchGate) {
        New-Item -ItemType Directory -Force -Path $musicRoot | Out-Null
    }
    if ($RunDownloadClientGate) {
        New-Item -ItemType Directory -Force -Path $downloadsRoot | Out-Null
    }

    $pluginNames = New-Object System.Collections.Generic.List[string]

    foreach ($spec in $PluginZip) {
        $parts = $spec.Split("=", 2)
        if ($parts.Count -ne 2) {
            throw "Invalid -PluginZip entry '$spec'. Expected: name=path"
        }

        $name = $parts[0].Trim()
        $zipPath = $parts[1].Trim().Trim('"')
        if ([string]::IsNullOrWhiteSpace($name)) {
            throw "Invalid -PluginZip entry '$spec' (empty name)."
        }
        if (-not (Test-Path $zipPath)) {
            throw "Plugin zip not found: $zipPath"
        }

        $folderName = Get-PluginFolderName $name
        $targetDir = Join-Path $pluginsRoot "$PluginsOwner/$folderName"
        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

        Expand-Archive -Path $zipPath -DestinationPath $targetDir -Force

        Write-Host "Staged $name => $targetDir" -ForegroundColor Green
        $pluginNames.Add($name.ToLowerInvariant()) | Out-Null
    }

    & docker rm -f $ContainerName 2>$null | Out-Null

    $pluginMount = $pluginsRoot.Replace('\', '/')
    $configMount = $configRoot.Replace('\', '/')
    $musicMount = $musicRoot.Replace('\', '/')
    $downloadsMount = $downloadsRoot.Replace('\', '/')

    $dockerArgs = @(
        "run", "-d",
        "--name", $ContainerName,
        "-p", "${Port}:8686",
        "-v", "${configMount}:/config",
        "-v", "${pluginMount}:/config/plugins:ro"
    )

    if ($RunSearchGate) {
        $dockerArgs += @("-v", "${musicMount}:/music")
    }
    if ($RunDownloadClientGate) {
        $dockerArgs += @("-v", "${downloadsMount}:/downloads")
    }

    $dockerArgs += @(
        "-e", "PUID=1000",
        "-e", "PGID=1000",
        "-e", "TZ=UTC",
        "ghcr.io/hotio/lidarr:$LidarrTag"
    )

    $startResult = & docker @dockerArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start container:`n$startResult"
    }

    $lidarrUrl = "http://localhost:$Port"

    Write-Host "Waiting for config.xml + API key..." -ForegroundColor Yellow
    $apiKey = $null
    $start = Get-Date
    while (((Get-Date) - $start).TotalSeconds -lt $StartupTimeoutSeconds) {
        $null = & docker exec $ContainerName sh -c "test -f /config/config.xml" 2>$null
        if ($LASTEXITCODE -eq 0) {
            $apiKey = & docker exec $ContainerName sh -c "sed -n 's:.*<ApiKey>\\(.*\\)</ApiKey>.*:\\1:p' /config/config.xml" 2>$null
            if (-not [string]::IsNullOrWhiteSpace($apiKey)) {
                break
            }
        }

        Start-Sleep -Seconds 2
    }

    if ([string]::IsNullOrWhiteSpace($apiKey)) {
        throw "Failed to extract Lidarr API key from config.xml within ${StartupTimeoutSeconds}s."
    }

    $headers = @{ "X-Api-Key" = $apiKey.Trim() }

    Write-Host "Waiting for Lidarr API..." -ForegroundColor Yellow
    $start = Get-Date
    $status = $null
    while (((Get-Date) - $start).TotalSeconds -lt $StartupTimeoutSeconds) {
        try {
            $resp = Invoke-WebRequest -Uri "$lidarrUrl/api/v1/system/status" -Headers $headers -TimeoutSec 5 -ErrorAction Stop
            if ($resp.StatusCode -eq 200) {
                $status = $resp.Content | ConvertFrom-Json
                break
            }
        }
        catch {
            Start-Sleep -Seconds 3
        }
    }

    if ($status -eq $null) {
        Write-Host "=== Container logs ===" -ForegroundColor Yellow
        & docker logs $ContainerName --tail 200 2>&1
        throw "Timeout waiting for Lidarr API at $lidarrUrl"
    }

    Write-Host "Lidarr online: v$($status.version)" -ForegroundColor Green

    Write-Host "Checking schemas for plugin implementations..." -ForegroundColor Yellow
    $schemaStart = Get-Date

    $indexerSchemas = $null
    $downloadClientSchemas = $null

    while (((Get-Date) - $schemaStart).TotalSeconds -lt $SchemaTimeoutSeconds) {
        try {
            $indexerResponse = Invoke-WebRequest -Uri "$lidarrUrl/api/v1/indexer/schema" -Headers $headers -TimeoutSec 10 -ErrorAction Stop
            $downloadResponse = Invoke-WebRequest -Uri "$lidarrUrl/api/v1/downloadclient/schema" -Headers $headers -TimeoutSec 10 -ErrorAction Stop

            $indexerSchemas = $indexerResponse.Content | ConvertFrom-Json
            $downloadClientSchemas = $downloadResponse.Content | ConvertFrom-Json

            if ($indexerSchemas -and $downloadClientSchemas) {
                break
            }
        }
        catch {
            Start-Sleep -Seconds 5
        }
    }

    if (-not $indexerSchemas -or -not $downloadClientSchemas) {
        throw "Failed to fetch schema endpoints within ${SchemaTimeoutSeconds}s."
    }

    $failed = $false

    foreach ($plugin in $pluginNames) {
        if (-not $expectations.ContainsKey($plugin)) {
            Write-Host "No expectations configured for '$plugin' (skipping schema assertions)" -ForegroundColor Yellow
            continue
        }

        $exp = $expectations[$plugin]

        foreach ($impl in $exp.Indexers) {
            $found = $indexerSchemas | Where-Object { $_.implementation -eq $impl }
            if ($found) {
                Write-Host "✓ indexer/schema contains $impl" -ForegroundColor Green
            }
            else {
                Write-Host "✗ indexer/schema missing $impl" -ForegroundColor Red
                $failed = $true
            }
        }

        foreach ($impl in $exp.DownloadClients) {
            $found = $downloadClientSchemas | Where-Object { $_.implementation -eq $impl }
            if ($found) {
                Write-Host "✓ downloadclient/schema contains $impl" -ForegroundColor Green
            }
            else {
                Write-Host "✗ downloadclient/schema missing $impl" -ForegroundColor Red
                $failed = $true
            }
        }
    }

    if ($failed) {
        Write-Host "`nAvailable indexer implementations (sample):" -ForegroundColor Yellow
        $indexerSchemas | ForEach-Object { $_.implementation } | Sort-Object -Unique | Select-Object -First 80 | ForEach-Object { Write-Host "  - $_" }

        Write-Host "`nAvailable download client implementations (sample):" -ForegroundColor Yellow
        $downloadClientSchemas | ForEach-Object { $_.implementation } | Sort-Object -Unique | Select-Object -First 80 | ForEach-Object { Write-Host "  - $_" }

        exit 1
    }

    $configuredIndexerNames = New-Object System.Collections.Generic.List[string]
    $configuredDownloadClientNames = New-Object System.Collections.Generic.List[string]

    if ($RunMediumGate) {
        Write-Host "`n=== Medium gate: configure + test indexers ===" -ForegroundColor Cyan

        $mediumConfigured = $false
        $mediumFailed = $false

        foreach ($plugin in $pluginNames) {
            if (-not $expectations.ContainsKey($plugin)) { continue }
            $exp = $expectations[$plugin]

            foreach ($impl in $exp.Indexers) {
                $schema = $indexerSchemas | Where-Object { $_.implementation -eq $impl } | Select-Object -First 1
                if (-not $schema) {
                    Write-Host "✗ medium gate: missing schema for $impl" -ForegroundColor Red
                    $mediumFailed = $true
                    continue
                }

                if ($impl -eq "QobuzIndexer") {
                    $qEmail = Get-NonEmptyEnvValue "QOBUZARR_EMAIL"
                    if ([string]::IsNullOrWhiteSpace($qEmail)) { $qEmail = Get-NonEmptyEnvValue "QOBUZ_EMAIL" }

                    $qPassword = Get-NonEmptyEnvValue "QOBUZARR_PASSWORD"
                    if ([string]::IsNullOrWhiteSpace($qPassword)) { $qPassword = Get-NonEmptyEnvValue "QOBUZ_PASSWORD" }

                    $qUserId = Get-NonEmptyEnvValue "QOBUZARR_USER_ID"
                    if ([string]::IsNullOrWhiteSpace($qUserId)) { $qUserId = Get-NonEmptyEnvValue "QOBUZ_USER_ID" }

                    $qAuthToken = Get-NonEmptyEnvValue "QOBUZARR_AUTH_TOKEN"
                    if ([string]::IsNullOrWhiteSpace($qAuthToken)) { $qAuthToken = Get-NonEmptyEnvValue "QOBUZ_AUTH_TOKEN" }

                    $qAuthMethod = Get-NonEmptyEnvValue "QOBUZARR_AUTH_METHOD"
                    if ([string]::IsNullOrWhiteSpace($qAuthMethod)) { $qAuthMethod = Get-NonEmptyEnvValue "QOBUZ_AUTH_METHOD" }

                    $qAppId = Get-NonEmptyEnvValue "QOBUZARR_APP_ID"
                    if ([string]::IsNullOrWhiteSpace($qAppId)) { $qAppId = Get-NonEmptyEnvValue "QOBUZ_APP_ID" }

                    $qAppSecret = Get-NonEmptyEnvValue "QOBUZARR_APP_SECRET"
                    if ([string]::IsNullOrWhiteSpace($qAppSecret)) { $qAppSecret = Get-NonEmptyEnvValue "QOBUZ_APP_SECRET" }

                    $qCountry = Get-NonEmptyEnvValue "QOBUZARR_COUNTRY_CODE"
                    if ([string]::IsNullOrWhiteSpace($qCountry)) { $qCountry = Get-NonEmptyEnvValue "QOBUZ_COUNTRY_CODE" }
                    if ([string]::IsNullOrWhiteSpace($qCountry)) { $qCountry = "US" }

                    $mode = $null
                    if (-not [string]::IsNullOrWhiteSpace($qAuthMethod)) {
                        if ($qAuthMethod.Equals("Token", [StringComparison]::OrdinalIgnoreCase)) { $mode = "Token" }
                        elseif ($qAuthMethod.Equals("Email", [StringComparison]::OrdinalIgnoreCase)) { $mode = "Email" }
                        else { throw "Invalid QOBUZARR_AUTH_METHOD='$qAuthMethod'. Expected: Email|Token." }
                    }
                    elseif (-not [string]::IsNullOrWhiteSpace($qUserId) -and -not [string]::IsNullOrWhiteSpace($qAuthToken)) {
                        $mode = "Token"
                    }
                    elseif (-not [string]::IsNullOrWhiteSpace($qEmail) -and -not [string]::IsNullOrWhiteSpace($qPassword)) {
                        $mode = "Email"
                    }

                    if ([string]::IsNullOrWhiteSpace($mode)) {
                        Write-Host "↷ medium gate: skipping QobuzIndexer (missing Qobuz credentials env vars)" -ForegroundColor Yellow
                        continue
                    }

                    $mediumConfigured = $true
                    $payload = Copy-JsonObject $schema
                    $payload.name = "SmokeTest - Qobuzarr"
                    $payload.enable = $true

                    if ($mode -eq "Email") {
                        Set-FieldValue -Schema $payload -CandidateNames @("authMethod", "AuthMethod") -Value 0
                        Set-FieldValue -Schema $payload -CandidateNames @("email", "Email") -Value $qEmail
                        Set-FieldValue -Schema $payload -CandidateNames @("password", "Password") -Value $qPassword
                    }
                    else {
                        Set-FieldValue -Schema $payload -CandidateNames @("authMethod", "AuthMethod") -Value 1
                        Set-FieldValue -Schema $payload -CandidateNames @("userId", "UserId") -Value $qUserId
                        Set-FieldValue -Schema $payload -CandidateNames @("authToken", "AuthToken") -Value $qAuthToken
                    }

                    Set-FieldValue -Schema $payload -CandidateNames @("countryCode", "CountryCode") -Value $qCountry
                    if (-not [string]::IsNullOrWhiteSpace($qAppId)) { Set-FieldValue -Schema $payload -CandidateNames @("appId", "AppId") -Value $qAppId }
                    if (-not [string]::IsNullOrWhiteSpace($qAppSecret)) { Set-FieldValue -Schema $payload -CandidateNames @("appSecret", "AppSecret") -Value $qAppSecret }

                    try {
                        $created = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/indexer" -Headers $headers -Body $payload -TimeoutSeconds $MediumGateTimeoutSeconds
                        $null = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/indexer/test" -Headers $headers -Body $created -TimeoutSeconds $MediumGateTimeoutSeconds
                        Write-Host "✓ medium gate: QobuzIndexer configured + test succeeded" -ForegroundColor Green
                        $configuredIndexerNames.Add($payload.name) | Out-Null
                    }
                    catch {
                        Write-Host "✗ medium gate: QobuzIndexer test failed`n$($_.Exception.Message)" -ForegroundColor Red
                        $mediumFailed = $true
                    }

                    continue
                }

                if ($impl -eq "TidalLidarrIndexer") {
                    $tRedirectUrl = Get-NonEmptyEnvValue "TIDALARR_REDIRECT_URL"
                    if ([string]::IsNullOrWhiteSpace($tRedirectUrl)) { $tRedirectUrl = Get-NonEmptyEnvValue "TIDAL_REDIRECT_URL" }

                    $tMarket = Get-NonEmptyEnvValue "TIDALARR_MARKET"
                    if ([string]::IsNullOrWhiteSpace($tMarket)) { $tMarket = Get-NonEmptyEnvValue "TIDAL_MARKET" }
                    if ([string]::IsNullOrWhiteSpace($tMarket)) { $tMarket = "US" }

                    if ([string]::IsNullOrWhiteSpace($tRedirectUrl)) {
                        Write-Host "↷ medium gate: skipping TidalLidarrIndexer (missing TIDALARR_REDIRECT_URL)" -ForegroundColor Yellow
                        continue
                    }

                    $mediumConfigured = $true
                    $payload = Copy-JsonObject $schema
                    $payload.name = "SmokeTest - Tidalarr"
                    $payload.enable = $true

                    Set-FieldValue -Schema $payload -CandidateNames @("configPath", "ConfigPath") -Value "/config/tidalarr"
                    Set-FieldValue -Schema $payload -CandidateNames @("redirectUrl", "RedirectUrl") -Value $tRedirectUrl
                    Set-FieldValue -Schema $payload -CandidateNames @("tidalMarket", "TidalMarket") -Value $tMarket

                    try {
                        $created = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/indexer" -Headers $headers -Body $payload -TimeoutSeconds $MediumGateTimeoutSeconds
                        $null = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/indexer/test" -Headers $headers -Body $created -TimeoutSeconds $MediumGateTimeoutSeconds
                        Write-Host "✓ medium gate: TidalLidarrIndexer configured + test succeeded" -ForegroundColor Green
                        $configuredIndexerNames.Add($payload.name) | Out-Null
                    }
                    catch {
                        Write-Host "✗ medium gate: TidalLidarrIndexer test failed`n$($_.Exception.Message)" -ForegroundColor Red
                        $mediumFailed = $true
                    }

                    continue
                }

                Write-Host "↷ medium gate: no config mapping for $impl (skipping)" -ForegroundColor Yellow
            }
        }

        if (-not $mediumConfigured) {
            Write-Host "↷ medium gate skipped (no supported credentials provided)." -ForegroundColor Yellow
        }
        elseif ($mediumFailed) {
            exit 1
        }
    }

    if ($RunDownloadClientGate) {
        Write-Host "`n=== Download client gate: configure + test download clients ===" -ForegroundColor Cyan

        $downloadConfigured = $false
        $downloadFailed = $false

        foreach ($plugin in $pluginNames) {
            if (-not $expectations.ContainsKey($plugin)) { continue }
            $exp = $expectations[$plugin]

            foreach ($impl in $exp.DownloadClients) {
                $schema = $downloadClientSchemas | Where-Object { $_.implementation -eq $impl } | Select-Object -First 1
                if (-not $schema) {
                    Write-Host "✗ download gate: missing schema for $impl" -ForegroundColor Red
                    $downloadFailed = $true
                    continue
                }

                if ($impl -eq "QobuzDownloadClient") {
                    if (-not $RunMediumGate -or $configuredIndexerNames.Count -eq 0) {
                        Write-Host "↷ download gate: skipping QobuzDownloadClient (indexer not configured; credentials may be unavailable)" -ForegroundColor Yellow
                        continue
                    }

                    $downloadConfigured = $true
                    $payload = Copy-JsonObject $schema
                    $payload.name = "SmokeTest - Qobuzarr DL"
                    $payload.enable = $true

                    Set-FieldValue -Schema $payload -CandidateNames @("downloadPath", "DownloadPath") -Value "/downloads/qobuzarr"

                    try {
                        $created = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/downloadclient" -Headers $headers -Body $payload -TimeoutSeconds $MediumGateTimeoutSeconds
                        $null = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/downloadclient/test" -Headers $headers -Body $created -TimeoutSeconds $MediumGateTimeoutSeconds
                        Write-Host "✓ download gate: QobuzDownloadClient configured + test succeeded" -ForegroundColor Green
                        $configuredDownloadClientNames.Add($payload.name) | Out-Null
                    }
                    catch {
                        Write-Host "✗ download gate: QobuzDownloadClient test failed`n$($_.Exception.Message)" -ForegroundColor Red
                        $downloadFailed = $true
                    }

                    continue
                }

                if ($impl -eq "TidalLidarrDownloadClient") {
                    $tRedirectUrl = Get-NonEmptyEnvValue "TIDALARR_REDIRECT_URL"
                    if ([string]::IsNullOrWhiteSpace($tRedirectUrl)) { $tRedirectUrl = Get-NonEmptyEnvValue "TIDAL_REDIRECT_URL" }

                    if ([string]::IsNullOrWhiteSpace($tRedirectUrl)) {
                        Write-Host "↷ download gate: skipping TidalLidarrDownloadClient (missing TIDALARR_REDIRECT_URL)" -ForegroundColor Yellow
                        continue
                    }

                    $downloadConfigured = $true
                    $payload = Copy-JsonObject $schema
                    $payload.name = "SmokeTest - Tidalarr DL"
                    $payload.enable = $true

                    Set-FieldValue -Schema $payload -CandidateNames @("configPath", "ConfigPath") -Value "/config/tidalarr"
                    Set-FieldValue -Schema $payload -CandidateNames @("redirectUrl", "RedirectUrl") -Value $tRedirectUrl
                    Set-FieldValue -Schema $payload -CandidateNames @("downloadPath", "DownloadPath") -Value "/downloads/tidalarr"

                    try {
                        $created = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/downloadclient" -Headers $headers -Body $payload -TimeoutSeconds $MediumGateTimeoutSeconds
                        $null = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/downloadclient/test" -Headers $headers -Body $created -TimeoutSeconds $MediumGateTimeoutSeconds
                        Write-Host "✓ download gate: TidalLidarrDownloadClient configured + test succeeded" -ForegroundColor Green
                        $configuredDownloadClientNames.Add($payload.name) | Out-Null
                    }
                    catch {
                        Write-Host "✗ download gate: TidalLidarrDownloadClient test failed`n$($_.Exception.Message)" -ForegroundColor Red
                        $downloadFailed = $true
                    }

                    continue
                }

                Write-Host "↷ download gate: no config mapping for $impl (skipping)" -ForegroundColor Yellow
            }
        }

        if (-not $downloadConfigured) {
            Write-Host "↷ download client gate skipped (no supported credentials provided)." -ForegroundColor Yellow
        }
        elseif ($downloadFailed) {
            exit 1
        }
    }

    if ($RunSearchGate) {
        Write-Host "`n=== Search gate: AlbumSearch + releases ===" -ForegroundColor Cyan

        if (-not $configuredIndexerNames -or $configuredIndexerNames.Count -eq 0) {
            Write-Host "↷ search gate skipped (no indexers configured in medium gate)." -ForegroundColor Yellow
        }
        else {
            $rootFolders = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/rootfolder" -Headers $headers -TimeoutSeconds 30
            $musicRootFolder = $rootFolders | Where-Object { $_.path -eq "/music" } | Select-Object -First 1

            if (-not $musicRootFolder) {
                $musicRootFolder = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/rootfolder" -Headers $headers -Body @{ path = "/music" } -TimeoutSeconds 30
            }

            if (-not $musicRootFolder -or $musicRootFolder.path -ne "/music") {
                throw "Failed to create/find root folder '/music' required for search gate."
            }

            $qualityProfiles = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/qualityprofile" -Headers $headers -TimeoutSeconds 30
            $metadataProfiles = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/metadataprofile" -Headers $headers -TimeoutSeconds 30

            $qualityProfileId = ($qualityProfiles | Select-Object -First 1).id
            $metadataProfileId = ($metadataProfiles | Select-Object -First 1).id

            if (-not $qualityProfileId -or -not $metadataProfileId) {
                throw "Failed to resolve quality/metadata profiles required for artist add."
            }

            $artistTerm = UrlEncode $SearchArtistTerm
            $artistLookup = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/artist/lookup?term=$artistTerm" -Headers $headers -TimeoutSeconds 30
            $artistCandidate = $artistLookup | Select-Object -First 1

            if (-not $artistCandidate) {
                throw "No artists returned from lookup for '$SearchArtistTerm'."
            }

            $artistPayload = Copy-JsonObject $artistCandidate
            $artistPayload.qualityProfileId = $qualityProfileId
            $artistPayload.metadataProfileId = $metadataProfileId
            $artistPayload.rootFolderPath = "/music"
            $artistPayload.monitored = $true
            $artistPayload.monitorNewItems = "all"
            $artistPayload.addOptions = @{
                monitor = "all"
                monitored = $true
                searchForMissingAlbums = $false
            }

            $createdArtist = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/artist" -Headers $headers -Body $artistPayload -TimeoutSeconds 60
            if (-not $createdArtist -or -not $createdArtist.id) {
                throw "Failed to create artist in Lidarr for search gate."
            }

            Write-Host "Seeded artist: $($createdArtist.artistName) (id=$($createdArtist.id))" -ForegroundColor Green

            $albums = $null
            $start = Get-Date
            while (((Get-Date) - $start).TotalSeconds -lt $SearchTimeoutSeconds) {
                $albums = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/album?artistId=$($createdArtist.id)&includeAllArtistAlbums=true" -Headers $headers -TimeoutSeconds 30
                if ($albums -and $albums.Count -gt 0) { break }
                Start-Sleep -Seconds 3
            }

            if (-not $albums -or $albums.Count -eq 0) {
                throw "Timeout waiting for albums to be available for artist '$($createdArtist.artistName)'."
            }

            $targetTitle = $SearchAlbumTitle.Trim()
            $album = $albums | Where-Object { $_.title -and $_.title.ToString().Equals($targetTitle, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
            if (-not $album) {
                $album = $albums | Where-Object { $_.title -and $_.title.ToString().IndexOf($targetTitle, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | Select-Object -First 1
            }

            if (-not $album) {
                $sample = ($albums | Select-Object -First 20 | ForEach-Object { $_.title }) -join ", "
                throw "Could not find an album matching '$SearchAlbumTitle' for artist '$($createdArtist.artistName)'. Sample albums: $sample"
            }

            Write-Host "Search gate album: $($album.title) (id=$($album.id))" -ForegroundColor Green

            $cmd = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/command" -Headers $headers -Body @{ name = "AlbumSearch"; albumIds = @($album.id) } -TimeoutSeconds 30
            if (-not $cmd -or -not $cmd.id) {
                throw "Failed to enqueue AlbumSearch command."
            }

            $null = Wait-LidarrCommandCompleted -CommandId $cmd.id -LidarrUrl $lidarrUrl -Headers $headers -TimeoutSeconds $SearchTimeoutSeconds

            $releases = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/release?albumId=$($album.id)" -Headers $headers -TimeoutSeconds 30
            $releaseCount = 0
            if ($releases) { $releaseCount = $releases.Count }

            if ($releaseCount -eq 0) {
                throw "Search gate failure: release list is empty for albumId=$($album.id)."
            }

            $seenIndexerNames = $releases | ForEach-Object { $_.indexer } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique
            Write-Host "Search gate releases: $releaseCount (indexers: $($seenIndexerNames -join ', '))" -ForegroundColor Green

            if ($RequireAllConfiguredIndexersInSearch) {
                $missingFromConfigured = @()
                foreach ($idxName in $configuredIndexerNames) {
                    if ($seenIndexerNames -notcontains $idxName) {
                        $missingFromConfigured += $idxName
                    }
                }

                if ($missingFromConfigured.Count -gt 0) {
                    throw "Search gate failure: no releases attributed to configured indexer(s): $($missingFromConfigured -join ', '). Consider using a different artist/album or verifying credentials."
                }
            }
        }
    }

    Write-Host "`n🎉 Multi-plugin schema smoke test passed." -ForegroundColor Green
}
finally {
    Cleanup
}
