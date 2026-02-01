#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Multi-plugin Docker smoke test for Lidarr plugins.

.DESCRIPTION
    Starts a Lidarr Docker container, mounts one or more plugin packages (zip files),
    waits for Lidarr to become available, then verifies plugin discovery via:
      - /api/v1/indexer/schema
      - /api/v1/downloadclient/schema
      - /api/v1/importlist/schema

    This is a "basic gate" intended to catch runtime load failures and type-identity
    mismatches when multiple plugins co-exist in the same Lidarr instance.

.PARAMETER LidarrTag
    Lidarr Docker image tag to run (plugins branch). Default: pr-plugins-3.1.1.4884

.PARAMETER LidarrImage
    Full Lidarr Docker image name (overrides LidarrTag).
    Example: ghcr.io/hotio/lidarr:pr-plugins-3.1.1.4884

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

.PARAMETER RunGrabGate
    Optional "grab gate" that POSTs one release back to Lidarr's `/api/v1/release`
    endpoint to initiate a download via a specific configured download client.
    This is credential-gated and depends on successful Medium + Download Client +
    Search gates.

.PARAMETER RunTelemetryDIGate
    Optional "telemetry DI gate" that verifies the IDownloadTelemetryService from
    Common was successfully resolved and invoked during a download operation.
    This gate depends on RunGrabGate (with RequireDownloadedFiles) since we need
    an actual completed download to trigger telemetry logging.
    The gate checks Lidarr container logs for telemetry entries (e.g., "Download completed:"
    with track/album IDs, byte counts, timing data) proving that DI resolution worked
    in the merged/internalized plugin context.

.PARAMETER RunGoldenPersistGate
    Optional "golden-persist gate" that runs the full workflow (search -> grab -> download),
    then restarts the Lidarr container and verifies:
      - Plugin still loads (schemas available)
      - Queue/history state persists (no duplicate grabs)
      - Telemetry signal still emitted
    This gate depends on RunGrabGate and RequireDownloadedFiles.

.PARAMETER RunAuthFailRedactionGate
    Optional "authfail-redaction gate" that configures plugins with intentionally bad
    credentials and verifies:
      - Operation fails in an expected way (HTTP 401/403/429, no crash)
      - Error responses do not leak secrets
      - Container logs do not leak secrets (query strings, bearer tokens redacted)
    This gate can run independently of other credential-gated tests.

.PARAMETER GrabTimeoutSeconds
    Max time to wait for a grabbed release to appear in Lidarr's queue. Default: 300

.PARAMETER TelemetryGateTimeoutSeconds
    Max time to wait for telemetry log entries to appear after download completes. Default: 60

.PARAMETER RequireDownloadedFiles
    When set, the grab gate also requires that at least one file appears under the
    configured download path for each plugin (e.g., `/downloads/qobuzarr`). This is
    intended for local validation and can be slow/flaky depending on album size.

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

.PARAMETER WorkRoot
    Override the working directory used for staging plugins/config/music/downloads.
    Default: `.docker-multi-smoke-test/<ContainerName>` under the repo root.

.PARAMETER PreserveState
    Preserve the work directory contents (especially `/config`) between runs.
    When set, only the plugin staging directory is refreshed each run.

.PARAMETER CleanState
    Deletes the WorkRoot directory before starting (useful with PreserveState).

.PARAMETER UseExistingConfigForSearchGate
    Allows `-RunSearchGate` to proceed even if no indexers were configured by the
    medium gate. When set, the search gate will use any already-configured/enabled
    indexers in the Lidarr instance.

.PARAMETER UseExistingConfigForDownloadClientGate
    If no download clients were configured by the download client gate, use any
    already-configured/enabled download clients in the Lidarr instance (matching
    expected plugin implementations) and run `/api/v1/downloadclient/test` for
    each.

.PARAMETER UseExistingConfigForGrabGate
    Allows the grab gate to run using already-configured/enabled indexers and
    download clients in the Lidarr instance (matching expected plugin
    implementations), instead of only those created by the medium/download gates.

.PARAMETER PluginsOwner
    Owner folder under /config/plugins. Default: RicherTunes

.PARAMETER HostBinPath
    Container path where Lidarr binaries live (used by HostOverrideAssembly mounts).
    Default: /app/bin

.PARAMETER HostOverrideAssembly
    One or more local DLL paths to mount into the Lidarr container's HostBinPath.
    Intended for local validation against an upstream fix before a new host image
    tag is published.

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
      -PluginZip @(
        "qobuzarr=D:\Alex\github\qobuzarr\artifacts\packages\qobuzarr-latest.zip",
        "tidalarr=D:\Alex\github\tidalarr\src\Tidalarr\artifacts\packages\tidalarr-latest.zip"
      ) `
      -RunMediumGate
#>

[CmdletBinding()]
param(
    [string]$LidarrTag = "pr-plugins-3.1.1.4884",
    [string]$LidarrImage,
    [string]$ContainerName = "lidarr-multi-plugin-smoke",
    [int]$Port = 8689,
    [int]$StartupTimeoutSeconds = 120,
    [int]$SchemaTimeoutSeconds = 60,
    [switch]$RunMediumGate,
    [int]$MediumGateTimeoutSeconds = 60,
    [switch]$RunDownloadClientGate,
    [switch]$RunSearchGate,
    [switch]$RunGrabGate,
    [int]$GrabTimeoutSeconds = 300,
    [switch]$RequireDownloadedFiles,
    [switch]$RunTelemetryDIGate,
    [int]$TelemetryGateTimeoutSeconds = 60,
    [switch]$RunGoldenPersistGate,
    [int]$GoldenPersistStartupTimeoutSeconds = 120,
    [switch]$RunAuthFailRedactionGate,
    [switch]$RunDriftSentinelGate,
    [switch]$DriftSentinelFailOnDrift,
    [switch]$DriftSentinelIncludeSuccessMode,
    [int]$SearchTimeoutSeconds = 180,
    [string]$SearchArtistTerm = "Miles Davis",
    [string]$SearchAlbumTitle = "Kind of Blue",
    [switch]$RequireAllConfiguredIndexersInSearch,
    [string[]]$PluginZip = @(),
    [string]$WorkRoot,
    [switch]$PreserveState,
    [switch]$CleanState,
    [switch]$UseExistingConfigForSearchGate,
    [switch]$UseExistingConfigForDownloadClientGate,
    [switch]$UseExistingConfigForGrabGate,
    [string]$PluginsOwner = "RicherTunes",
    [string]$HostBinPath = "/app/bin",
    [string[]]$HostOverrideAssembly = @(),
    [switch]$KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

# Import e2e-gates module for preflight packaging validation
Import-Module (Join-Path $scriptDir "lib/e2e-gates.psm1") -Force
# Import abstractions validation module
Import-Module (Join-Path $scriptDir "lib/e2e-abstractions.psm1") -Force
# Import release selection module for deterministic release selection
Import-Module (Join-Path $scriptDir "lib/e2e-release-selection.psm1") -Force
# Import shared E2E helpers (polling, assertions, log checking)
Import-Module (Join-Path $scriptDir "lib/e2e-helpers.psm1") -Force
# Import persistence module for golden-persist gate
Import-Module (Join-Path $scriptDir "lib/e2e-persistence.psm1") -Force
# Import auth-fail module for authfail-redaction gate
Import-Module (Join-Path $scriptDir "lib/e2e-authfail.psm1") -Force
# Import stub-http module for hermetic E2E testing
Import-Module (Join-Path $scriptDir "lib/e2e-stub-http.psm1") -Force
# Import drift sentinel module for stub-vs-live drift detection
Import-Module (Join-Path $scriptDir "lib/e2e-drift-sentinel.psm1") -Force

$image = if ([string]::IsNullOrWhiteSpace($LidarrImage)) { "ghcr.io/hotio/lidarr:$LidarrTag" } else { $LidarrImage.Trim() }

if ($RunSearchGate) {
    $RunMediumGate = $true
}
if ($RunDownloadClientGate) {
    $RunMediumGate = $true
}
if ($RunGrabGate) {
    $RunSearchGate = $true
    $RunDownloadClientGate = $true
    $RunMediumGate = $true
}
if ($RunTelemetryDIGate) {
    # Telemetry gate requires an actual completed download to verify DI resolution
    $RunGrabGate = $true
    $RequireDownloadedFiles = $true
    $RunSearchGate = $true
    $RunDownloadClientGate = $true
    $RunMediumGate = $true
}
if ($RunGoldenPersistGate) {
    # Golden-persist gate requires a completed download to verify persistence after restart
    $RunGrabGate = $true
    $RequireDownloadedFiles = $true
    $RunSearchGate = $true
    $RunDownloadClientGate = $true
    $RunMediumGate = $true
}

$expectations = @{
    "qobuzarr" = @{
        Indexers = @("QobuzIndexer")
        DownloadClients = @("QobuzDownloadClient")
        ImportLists = @()
    }
    "tidalarr" = @{
        Indexers = @("TidalLidarrIndexer")
        DownloadClients = @("TidalLidarrDownloadClient")
        ImportLists = @()
    }
    "brainarr" = @{
        Indexers = @()
        DownloadClients = @()
        ImportLists = @("Brainarr")
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

function Get-TargetFrameworkMajorVersion {
    param([Parameter(Mandatory = $false)][AllowNull()][string]$TargetFrameworkMoniker)

    if ([string]::IsNullOrWhiteSpace($TargetFrameworkMoniker)) { return $null }

    $tfm = $TargetFrameworkMoniker.Trim()
    if ($tfm -match '^net(?<major>\d+)\.') {
        return [int]$Matches['major']
    }

    return $null
}

function Get-PluginTargetFrameworkFromZip {
    param([Parameter(Mandatory = $true)][string]$ZipPath)

    if (-not (Test-Path -LiteralPath $ZipPath)) { return $null }

    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
        $entry = $archive.GetEntry('plugin.json')
        if (-not $entry) { return $null }

        $reader = $null
        try {
            $reader = [System.IO.StreamReader]::new($entry.Open())
            $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            if ($reader) { $reader.Dispose() }
        }

        $tfm = $null
        if ($manifest.PSObject.Properties.Name -contains 'targetFramework') {
            $tfm = $manifest.targetFramework
        }
        elseif ($manifest.PSObject.Properties.Name -contains 'targetFrameworks' -and $manifest.targetFrameworks) {
            $tfm = @($manifest.targetFrameworks)[0]
        }

        if ([string]::IsNullOrWhiteSpace($tfm)) { return $null }
        return $tfm.ToString().Trim()
    }
    catch {
        return $null
    }
    finally {
        if ($archive) { $archive.Dispose() }
    }
}

function Get-LidarrHostTargetFrameworkFromImage {
    param([Parameter(Mandatory = $true)][string]$LidarrImage)
    $cid = $null
    $tmp = $null
    try {
        $cid = (& docker create $LidarrImage 2>$null).Trim()
        if ([string]::IsNullOrWhiteSpace($cid)) { return $null }

        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("lidarr-runtimeconfig-" + [Guid]::NewGuid().ToString("N") + ".json")
        $copied = $false
        foreach ($candidate in @(
            "/app/bin/Lidarr.runtimeconfig.json",
            "/opt/lidarr/bin/Lidarr.runtimeconfig.json"
        )) {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            & docker cp "$cid`:$candidate" $tmp 2>$null
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $tmp)) {
                $copied = $true
                break
            }
        }

        if (-not $copied) { return $null }
        return ((Get-Content -LiteralPath $tmp -Raw | ConvertFrom-Json).runtimeOptions.tfm)
    }
    finally {
        if ($tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        if (-not [string]::IsNullOrWhiteSpace($cid)) { & docker rm $cid 2>$null | Out-Null }
    }
}

function Assert-HostSupportsPlugins {
    param(
        [Parameter(Mandatory = $true)][string]$LidarrImage,
        [Parameter(Mandatory = $true)][string]$LidarrTag,
        [Parameter(Mandatory = $true)][string[]]$ZipPaths
    )

    $hostTfm = Get-LidarrHostTargetFrameworkFromImage -LidarrImage $LidarrImage
    if ([string]::IsNullOrWhiteSpace($hostTfm)) {
        Write-Host "Could not determine host TFM from Lidarr image '$LidarrImage' (skipping TFM compatibility check)." -ForegroundColor Yellow
        return
    }

    $hostMajor = Get-TargetFrameworkMajorVersion -TargetFrameworkMoniker $hostTfm
    if (-not $hostMajor) {
        Write-Host "Could not parse host TFM '$hostTfm' (skipping TFM compatibility check)." -ForegroundColor Yellow
        return
    }

    $pluginTfms = @()
    foreach ($zip in $ZipPaths) {
        $tfm = Get-PluginTargetFrameworkFromZip -ZipPath $zip
        if (-not [string]::IsNullOrWhiteSpace($tfm)) {
            $pluginTfms += $tfm
        }
    }

    if ($pluginTfms.Count -eq 0) {
        Write-Host "No plugin targetFramework found in plugin.json (skipping TFM compatibility check)." -ForegroundColor Yellow
        return
    }

    $pluginMajors = @(
        $pluginTfms |
            ForEach-Object { Get-TargetFrameworkMajorVersion -TargetFrameworkMoniker $_ } |
            Where-Object { $_ }
    )
    if ($pluginMajors.Count -eq 0) {
        Write-Host "Could not parse plugin targetFramework values ($($pluginTfms -join ', ')) (skipping TFM compatibility check)." -ForegroundColor Yellow
        return
    }

    $requiredMajor = ($pluginMajors | Measure-Object -Maximum).Maximum
    if ($hostMajor -lt $requiredMajor) {
        throw "TFM mismatch: Lidarr host '$LidarrTag' targets '$hostTfm' but plugin(s) require net$requiredMajor.0 ($($pluginTfms -join ', ')). Use a net$requiredMajor host tag (e.g. pr-plugins-3.1.1.4884 for net8) or build net$hostMajor plugins."
    }
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

function Get-LidarrQueueRecords {
    param([Parameter(Mandatory = $false)][AllowNull()]$QueueResponse)

    if ($null -eq $QueueResponse) { return @() }

    $props = $QueueResponse.PSObject.Properties.Name
    if ($props -contains 'records') {
        if ($QueueResponse.records) { return @($QueueResponse.records) }
        return @()
    }

    return @($QueueResponse)
}

function Wait-DirectoryHasAnyFiles {
    param(
        [Parameter(Mandatory = $true)][string]$DirectoryPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $start = Get-Date
    while (((Get-Date) - $start).TotalSeconds -lt $TimeoutSeconds) {
        if (Test-Path -LiteralPath $DirectoryPath) {
            $file = Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($file) { return $file }
        }

        Start-Sleep -Seconds 5
    }

    return $null
}

function Invoke-InContainerApiGet {
    param(
        [Parameter(Mandatory = $true)][string]$ContainerName,
        [Parameter(Mandatory = $true)][string]$ApiPath,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter()][int]$RetryCount = 3,
        [Parameter()][int]$BaseDelayMs = 500
    )

    $url = "http://localhost:8686${ApiPath}"
    $headerArgs = @()
    foreach ($key in $Headers.Keys) {
        $headerArgs += @("-H", "${key}: $($Headers[$key])")
    }

    $attempt = 0
    while ($attempt -le $RetryCount) {
        $attempt++

        try {
            $output = & docker exec $ContainerName curl -sS -w "`n%{http_code}" -X GET @headerArgs $url 2>&1
            $lines = $output -split "`n"
            $statusCode = [int]$lines[-1].Trim()
            $body = $lines[0..($lines.Count - 2)] -join "`n"

            if ($statusCode -eq 200) {
                return $body
            }

            if ($statusCode -eq 404 -or $statusCode -eq 500 -or $statusCode -ge 501) {
                throw "HTTP $statusCode (non-retryable): $body"
            }
        }
        catch {
            if ($attempt -gt $RetryCount) { throw }
        }

        $delay = [Math]::Pow(2, $attempt - 1) * $BaseDelayMs
        $delayMs = [Math]::Min($delay, 5000)
        Start-Sleep -Milliseconds $delayMs
    }

    throw "Failed after ${RetryCount} retries for $ApiPath"
}

function Test-LidarrApiWithBackoff {
    param(
        [Parameter(Mandatory = $true)][string]$ContainerName,
        [Parameter(Mandatory = $true)][string]$LidarrUrl,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter()][switch]$UseInContainerProbe = $true
    )

    $start = Get-Date
    $attempt = 0

    while (((Get-Date) - $start).TotalSeconds -lt $TimeoutSeconds) {
        $attempt++
        $elapsed = ((Get-Date) - $start).TotalSeconds
        Write-Host "[$attempt] Checking Lidarr API... (${elapsed:F1}s elapsed)" -ForegroundColor DarkGray

        try {
            if ($UseInContainerProbe) {
                try {
                    $body = Invoke-InContainerApiGet -ContainerName $ContainerName -ApiPath "/api/v1/system/status" -Headers $Headers -RetryCount 2 -BaseDelayMs 200
                    if (-not [string]::IsNullOrWhiteSpace($body)) {
                        $status = $body | ConvertFrom-Json
                        if ($status -and $status.version) {
                            Write-Host "In-container probe succeeded: v$($status.version)" -ForegroundColor Green
                            return $status
                        }
                    }
                }
                catch {
                    Write-Host "In-container probe failed (fallback to host): $($_.Exception.Message)" -ForegroundColor Yellow
                }
            }

            $resp = Invoke-WebRequest -Uri "$LidarrUrl/api/v1/system/status" -Headers $headers -TimeoutSec 5 -ErrorAction Stop
            if ($resp.StatusCode -eq 200) {
                $status = $resp.Content | ConvertFrom-Json
                Write-Host "Host-side probe succeeded: v$($status.version)" -ForegroundColor Green
                return $status
            }
        }
        catch {
            $backoff = [Math]::Min([Math]::Pow(2, $attempt - 1) * 1, 10)
            Write-Host "  Retry in ${backoff}s..." -ForegroundColor DarkGray
            Start-Sleep -Seconds $backoff
        }
    }

    return $null
}

function Cleanup {
    if (-not $KeepRunning) {
        & docker rm -f $ContainerName 2>$null | Out-Null
    }
}

trap {
    $err = $_
    Cleanup
    throw $err
}

try {
    Ensure-DockerAvailable

    if ($PluginZip.Count -eq 0) {
        throw "No plugins specified. Provide at least one -PluginZip name=path argument."
    }

    Write-Host "=== Multi-Plugin Docker Smoke Test ===" -ForegroundColor Cyan
    Write-Host "Lidarr tag: $LidarrTag"
    Write-Host "Lidarr image: $image"
    Write-Host "Container: $ContainerName"
    Write-Host "Port: $Port"

    $workRootBase = if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
        Join-Path $repoRoot ".docker-multi-smoke-test"
    }
    else {
        $WorkRoot.Trim()
    }

    $workRoot = Join-Path $workRootBase $ContainerName
    $pluginsRoot = Join-Path $workRoot "plugins"
    $configRoot = Join-Path $workRoot "config"
    $musicRoot = Join-Path $workRoot "music"
    $downloadsRoot = Join-Path $workRoot "downloads"

    if ($CleanState -and (Test-Path $workRoot)) {
        Remove-Item -Recurse -Force $workRoot
    }
    elseif ((-not $PreserveState) -and (Test-Path $workRoot)) {
        Remove-Item -Recurse -Force $workRoot
    }

    if (Test-Path $pluginsRoot) {
        Remove-Item -Recurse -Force $pluginsRoot
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
    $pluginZipPaths = New-Object System.Collections.Generic.List[string]

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

        # Preflight: Validate package doesn't contain host-provided DLLs
        $preflightResult = Test-PackagingPreflight -PluginPath $zipPath
        if (-not $preflightResult.Success) {
            throw "Packaging preflight failed for '$name': $($preflightResult.Errors -join '; ')"
        }

        $pluginZipPaths.Add((Resolve-Path -LiteralPath $zipPath).Path) | Out-Null

        $folderName = Get-PluginFolderName $name
        $targetDir = Join-Path $pluginsRoot "$PluginsOwner/$folderName"
        New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

        Expand-Archive -Path $zipPath -DestinationPath $targetDir -Force

        Write-Host "Staged $name => $targetDir" -ForegroundColor Green    
        $pluginNames.Add($name.ToLowerInvariant()) | Out-Null
    }

    Normalize-PluginAbstractions -PluginsRoot $pluginsRoot

    Assert-HostSupportsPlugins -LidarrImage $image -LidarrTag $LidarrTag -ZipPaths $pluginZipPaths.ToArray()

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
        "-v", "${pluginMount}:/config/plugins"
    )

    if ($HostOverrideAssembly.Count -gt 0) {
        $hostBin = $HostBinPath.TrimEnd('/')
        foreach ($overridePath in $HostOverrideAssembly) {
            if ([string]::IsNullOrWhiteSpace($overridePath)) { continue }
            if (-not (Test-Path -LiteralPath $overridePath)) {
                throw "HostOverrideAssembly file not found: $overridePath"
            }

            $fileName = [System.IO.Path]::GetFileName($overridePath)
            if ([string]::IsNullOrWhiteSpace($fileName)) {
                throw "HostOverrideAssembly path has no file name: $overridePath"
            }

            $src = (Resolve-Path -LiteralPath $overridePath).Path.Replace('\', '/')
            $dst = "$hostBin/$fileName"
            $dockerArgs += @("-v", "${src}:${dst}:ro")
        }

        Write-Host "Host overrides:" -ForegroundColor Yellow
        foreach ($overridePath in $HostOverrideAssembly) {
            if ([string]::IsNullOrWhiteSpace($overridePath)) { continue }
            Write-Host "  - $overridePath -> $HostBinPath" -ForegroundColor Yellow
        }
    }

    # Always mount /music and /downloads - unconditional mounts prevent false negatives
    # where tests pass because the container couldn't write to non-existent mount points
    $dockerArgs += @("-v", "${musicMount}:/music")
    $dockerArgs += @("-v", "${downloadsMount}:/downloads")

    $dockerArgs += @(
        "-e", "PUID=1000",
        "-e", "PGID=1000",
        "-e", "TZ=UTC",
        $image
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
            $apiKey = & docker exec $ContainerName sh -c "sed -n 's:.*<ApiKey>\(.*\)</ApiKey>.*:\1:p' /config/config.xml" 2>$null
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
    $status = Test-LidarrApiWithBackoff -ContainerName $ContainerName -LidarrUrl $lidarrUrl -Headers $headers -TimeoutSeconds $StartupTimeoutSeconds -UseInContainerProbe

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
    $importListSchemas = $null
    $anyImportListsExpected = $false
    foreach ($plugin in $pluginNames) {
        if (-not $expectations.ContainsKey($plugin)) { continue }
        if (@($expectations[$plugin].ImportLists).Count -gt 0) {
            $anyImportListsExpected = $true
            break
        }
    }

    $schemaAttempt = 0
    while (((Get-Date) - $schemaStart).TotalSeconds -lt $SchemaTimeoutSeconds) {
        $schemaAttempt++
        $elapsed = ((Get-Date) - $schemaStart).TotalSeconds
        Write-Host "[$schemaAttempt] Fetching schemas... (${elapsed:F1}s elapsed)" -ForegroundColor DarkGray

        try {
            try {
                $indexerBody = Invoke-InContainerApiGet -ContainerName $ContainerName -ApiPath "/api/v1/indexer/schema" -Headers $headers -RetryCount 2 -BaseDelayMs 300
                $downloadBody = Invoke-InContainerApiGet -ContainerName $ContainerName -ApiPath "/api/v1/downloadclient/schema" -Headers $headers -RetryCount 2 -BaseDelayMs 300
                if ($anyImportListsExpected) {
                    $importListBody = Invoke-InContainerApiGet -ContainerName $ContainerName -ApiPath "/api/v1/importlist/schema" -Headers $headers -RetryCount 2 -BaseDelayMs 300
                }

                $indexerSchemas = $indexerBody | ConvertFrom-Json
                $downloadClientSchemas = $downloadBody | ConvertFrom-Json
                if ($anyImportListsExpected) {
                    $importListSchemas = $importListBody | ConvertFrom-Json
                }

                Write-Host "In-container schema fetch succeeded" -ForegroundColor Green
                break
            }
            catch {
                Write-Host "In-container schema fetch failed (fallback to host): $($_.Exception.Message)" -ForegroundColor Yellow
            }

            $indexerResponse = Invoke-WebRequest -Uri "$lidarrUrl/api/v1/indexer/schema" -Headers $headers -TimeoutSec 10 -ErrorAction Stop
            $downloadResponse = Invoke-WebRequest -Uri "$lidarrUrl/api/v1/downloadclient/schema" -Headers $headers -TimeoutSec 10 -ErrorAction Stop
            if ($anyImportListsExpected) {
                $importListResponse = Invoke-WebRequest -Uri "$lidarrUrl/api/v1/importlist/schema" -Headers $headers -TimeoutSec 10 -ErrorAction Stop
            }

            $indexerSchemas = $indexerResponse.Content | ConvertFrom-Json
            $downloadClientSchemas = $downloadResponse.Content | ConvertFrom-Json
            if ($anyImportListsExpected) {
                $importListSchemas = $importListResponse.Content | ConvertFrom-Json
            }

            Write-Host "Host-side schema fetch succeeded" -ForegroundColor Green
            break
        }
        catch {
            $backoff = [Math]::Min([Math]::Pow(2, $schemaAttempt - 1) * 2, 10)
            Write-Host "  Retry in ${backoff}s..." -ForegroundColor DarkGray
            Start-Sleep -Seconds $backoff
        }
    }

    if (-not $indexerSchemas -or -not $downloadClientSchemas -or ($anyImportListsExpected -and -not $importListSchemas)) {
        $missing = @()
        if (-not $indexerSchemas) { $missing += "/api/v1/indexer/schema" }
        if (-not $downloadClientSchemas) { $missing += "/api/v1/downloadclient/schema" }
        if ($anyImportListsExpected -and -not $importListSchemas) { $missing += "/api/v1/importlist/schema" }
        throw "Failed to fetch schema endpoints within ${SchemaTimeoutSeconds}s (missing: $($missing -join ', '))."
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

        foreach ($impl in $exp.ImportLists) {
            $found = $importListSchemas | Where-Object { $_.implementation -eq $impl }
            if ($found) {
                Write-Host "✓ importlist/schema contains $impl" -ForegroundColor Green
            }
            else {
                Write-Host "✗ importlist/schema missing $impl" -ForegroundColor Red
                $failed = $true
            }
        }
    }

    if ($failed) {
        Write-Host "`nAvailable indexer implementations (sample):" -ForegroundColor Yellow
        $indexerSchemas | ForEach-Object { $_.implementation } | Sort-Object -Unique | Select-Object -First 80 | ForEach-Object { Write-Host "  - $_" }

        Write-Host "`nAvailable download client implementations (sample):" -ForegroundColor Yellow
        $downloadClientSchemas | ForEach-Object { $_.implementation } | Sort-Object -Unique | Select-Object -First 80 | ForEach-Object { Write-Host "  - $_" }

        if ($importListSchemas) {
            Write-Host "`nAvailable import list implementations (sample):" -ForegroundColor Yellow
            $importListSchemas | ForEach-Object { $_.implementation } | Sort-Object -Unique | Select-Object -First 80 | ForEach-Object { Write-Host "  - $_" }
        }

        exit 1
    }

    $configuredIndexerNames = New-Object System.Collections.Generic.List[string]
    $configuredDownloadClientNames = New-Object System.Collections.Generic.List[string]

    $configuredIndexerNameByImplementation = @{}
    $configuredDownloadClientIdByImplementation = @{}
    $configuredDownloadClientNameByImplementation = @{}

    $searchGateAlbum = $null
    $searchGateReleases = $null

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
                        $configuredIndexerNameByImplementation[$impl] = $payload.name
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
                        $configuredIndexerNameByImplementation[$impl] = $payload.name
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

        if ($UseExistingConfigForDownloadClientGate) {
            try {
                $existingDownloadClients = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/downloadclient" -Headers $headers -TimeoutSeconds 30
                $enabled = @($existingDownloadClients | Where-Object { $_.enable -eq $true })

                foreach ($plugin in $pluginNames) {
                    if (-not $expectations.ContainsKey($plugin)) { continue }
                    $exp = $expectations[$plugin]

                    foreach ($impl in $exp.DownloadClients) {
                        $match = $enabled | Where-Object { $_.implementation -eq $impl } | Select-Object -First 1
                        if (-not $match) { continue }

                        $downloadConfigured = $true
                        $configuredDownloadClientNames.Add($match.name) | Out-Null
                        $configuredDownloadClientIdByImplementation[$impl] = $match.id
                        $configuredDownloadClientNameByImplementation[$impl] = $match.name

                        try {
                            $null = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/downloadclient/test" -Headers $headers -Body $match -TimeoutSeconds $MediumGateTimeoutSeconds
                            Write-Host "✓ download gate: existing '$($match.name)' ($impl) test succeeded" -ForegroundColor Green
                        }
                        catch {
                            Write-Host "✗ download gate: existing '$($match.name)' ($impl) test failed`n$($_.Exception.Message)" -ForegroundColor Red
                            $downloadFailed = $true
                        }
                    }
                }
            }
            catch {
                Write-Host "↷ download gate: failed to query existing download clients`n$($_.Exception.Message)" -ForegroundColor Yellow
            }
        }

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
                        $configuredDownloadClientIdByImplementation[$impl] = $created.id
                        $configuredDownloadClientNameByImplementation[$impl] = $payload.name
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
                        $configuredDownloadClientIdByImplementation[$impl] = $created.id
                        $configuredDownloadClientNameByImplementation[$impl] = $payload.name
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

        if ((-not $configuredIndexerNames -or $configuredIndexerNames.Count -eq 0) -and $UseExistingConfigForSearchGate) {
            $existingIndexers = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/indexer" -Headers $headers -TimeoutSeconds 30
            $enabled = $existingIndexers | Where-Object { $_.enable -eq $true -and -not [string]::IsNullOrWhiteSpace($_.name) }
            foreach ($idx in $enabled) {
                $configuredIndexerNames.Add($idx.name) | Out-Null
            }
        }

        if (-not $configuredIndexerNames -or $configuredIndexerNames.Count -eq 0) {
            Write-Host "↷ search gate skipped (no indexers configured)." -ForegroundColor Yellow
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

            $searchGateAlbum = $album
            $searchGateReleases = $releases
        }
    }

    if ($RunGrabGate) {
        Write-Host "`n=== Grab gate: queue a release download ===" -ForegroundColor Cyan

        if (-not $searchGateAlbum -or -not $searchGateReleases -or $searchGateReleases.Count -eq 0) {
            throw "Grab gate requested, but search gate did not produce releases. Verify credentials and set -RunSearchGate."
        }

        if ($UseExistingConfigForGrabGate) {
            try {
                $existingIndexers = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/indexer" -Headers $headers -TimeoutSeconds 30
                $enabledIndexers = @($existingIndexers | Where-Object { $_.enable -eq $true })
                foreach ($plugin in $pluginNames) {
                    if (-not $expectations.ContainsKey($plugin)) { continue }
                    $exp = $expectations[$plugin]
                    foreach ($impl in $exp.Indexers) {
                        if ($configuredIndexerNameByImplementation.ContainsKey($impl)) { continue }
                        $match = $enabledIndexers | Where-Object { $_.implementation -eq $impl } | Select-Object -First 1
                        if ($match -and -not [string]::IsNullOrWhiteSpace($match.name)) {
                            $configuredIndexerNames.Add($match.name) | Out-Null
                            $configuredIndexerNameByImplementation[$impl] = $match.name
                        }
                    }
                }
            }
            catch {
            }

            try {
                $existingDownloadClients = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/downloadclient" -Headers $headers -TimeoutSeconds 30
                $enabledDownloadClients = @($existingDownloadClients | Where-Object { $_.enable -eq $true })
                foreach ($plugin in $pluginNames) {
                    if (-not $expectations.ContainsKey($plugin)) { continue }
                    $exp = $expectations[$plugin]
                    foreach ($impl in $exp.DownloadClients) {
                        if ($configuredDownloadClientIdByImplementation.ContainsKey($impl)) { continue }
                        $match = $enabledDownloadClients | Where-Object { $_.implementation -eq $impl } | Select-Object -First 1
                        if ($match -and $match.id -and -not [string]::IsNullOrWhiteSpace($match.name)) {
                            $configuredDownloadClientNames.Add($match.name) | Out-Null
                            $configuredDownloadClientIdByImplementation[$impl] = $match.id
                            $configuredDownloadClientNameByImplementation[$impl] = $match.name
                        }
                    }
                }
            }
            catch {
            }
        }

        $grabRequests = New-Object System.Collections.Generic.List[object]
        $grabFailed = $false

        foreach ($plugin in $pluginNames) {
            if (-not $expectations.ContainsKey($plugin)) { continue }

            $exp = $expectations[$plugin]
            $indexerImpl = @($exp.Indexers)[0]
            $downloadImpl = @($exp.DownloadClients)[0]

            $indexerName = $null
            if ($configuredIndexerNameByImplementation.ContainsKey($indexerImpl)) {
                $indexerName = $configuredIndexerNameByImplementation[$indexerImpl]
            }

            $downloadClientId = $null
            if ($configuredDownloadClientIdByImplementation.ContainsKey($downloadImpl)) {
                $downloadClientId = $configuredDownloadClientIdByImplementation[$downloadImpl]
            }

            $downloadClientName = $null
            if ($configuredDownloadClientNameByImplementation.ContainsKey($downloadImpl)) {
                $downloadClientName = $configuredDownloadClientNameByImplementation[$downloadImpl]
            }

            if ([string]::IsNullOrWhiteSpace($indexerName) -or [string]::IsNullOrWhiteSpace($downloadClientName) -or -not $downloadClientId) {
                Write-Host "↷ grab gate: skipping $plugin (indexer=$indexerImpl, downloadClient=$downloadImpl not configured)" -ForegroundColor Yellow
                continue
            }

            $candidates = @($searchGateReleases | Where-Object { $_.indexer -eq $indexerName -and $_.downloadAllowed -ne $false })
            # Select smallest release (for faster smoke tests) using shared deterministic helper
            $release = Select-DeterministicRelease -Releases $candidates -SizeAscending
            if (-not $release) {
                Write-Host "✗ grab gate: no releases attributed to indexer '$indexerName' (plugin=$plugin). Consider a different SearchArtistTerm/SearchAlbumTitle." -ForegroundColor Red
                $grabFailed = $true
                continue
            }

            $payload = Copy-JsonObject $release
            $payload | Add-Member -NotePropertyName "downloadClientId" -NotePropertyValue $downloadClientId -Force

            try {
                $null = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/release" -Headers $headers -Body $payload -TimeoutSeconds 60

                $mb = $null
                try { if ($release.size) { $mb = [Math]::Round(([double]$release.size) / 1MB, 1) } } catch { }
                $sizeLabel = if ($mb -ne $null) { " (${mb}MB)" } else { "" }
                Write-Host "✓ grab gate: queued '$($release.title)'$sizeLabel via indexer '$indexerName' -> download client '$downloadClientName'" -ForegroundColor Green

                $grabRequests.Add([pscustomobject]@{
                    Plugin = $plugin
                    IndexerName = $indexerName
                    DownloadClientName = $downloadClientName
                    DownloadClientId = $downloadClientId
                    DownloadDir = (Join-Path $downloadsRoot $plugin)
                }) | Out-Null
            }
            catch {
                Write-Host "✗ grab gate: failed to queue release via '$downloadClientName'`n$($_.Exception.Message)" -ForegroundColor Red
                $grabFailed = $true
            }
        }

        if ($grabFailed) { exit 1 }
        if ($grabRequests.Count -eq 0) {
            throw "Grab gate requested, but no grab requests were issued (no supported plugins configured)."
        }

        $queueSeen = @{}
        $queueStart = Get-Date
        while (((Get-Date) - $queueStart).TotalSeconds -lt $GrabTimeoutSeconds) {
            $queue = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/queue?page=1&pageSize=50" -Headers $headers -TimeoutSeconds 30
            $records = Get-LidarrQueueRecords -QueueResponse $queue

            foreach ($req in $grabRequests) {
                $key = "$($req.Plugin)|$($req.DownloadClientName)"
                if ($queueSeen.ContainsKey($key)) { continue }

                $match = $records | Where-Object {
                    $_.downloadClient -eq $req.DownloadClientName -and $_.albumId -eq $searchGateAlbum.id
                } | Select-Object -First 1

                if (-not $match) {
                    $match = $records | Where-Object {
                        $_.downloadClient -eq $req.DownloadClientName -and $_.title -and $_.title.ToString().IndexOf($searchGateAlbum.title, [StringComparison]::OrdinalIgnoreCase) -ge 0
                    } | Select-Object -First 1
                }

                if ($match) {
                    if ($match.status -eq "failed" -or -not [string]::IsNullOrWhiteSpace($match.errorMessage)) {
                        $err = $match.errorMessage
                        if ([string]::IsNullOrWhiteSpace($err)) { $err = "Queue item status='$($match.status)'." }
                        throw "Grab gate failure: queue item for '$($req.DownloadClientName)' reported an error: $err"
                    }

                    $queueSeen[$key] = $match
                    Write-Host "✓ grab gate: queue item observed for '$($req.DownloadClientName)' (status=$($match.status))" -ForegroundColor Green
                }
            }

            if ($queueSeen.Count -ge $grabRequests.Count) { break }
            Start-Sleep -Seconds 5
        }

        if ($queueSeen.Count -lt $grabRequests.Count) {
            $missing = $grabRequests | Where-Object { -not $queueSeen.ContainsKey("$($_.Plugin)|$($_.DownloadClientName)") } | ForEach-Object { $_.DownloadClientName }
            throw "Grab gate failure: did not observe queue items within ${GrabTimeoutSeconds}s for: $($missing -join ', ')"
        }

        if ($RequireDownloadedFiles) {
            Write-Host "`n=== Grab gate: verify download artifacts exist ===" -ForegroundColor Cyan

            foreach ($req in $grabRequests) {
                $downloadDir = $req.DownloadDir
                $file = Wait-DirectoryHasAnyFiles -DirectoryPath $downloadDir -TimeoutSeconds $GrabTimeoutSeconds
                if (-not $file) {
                    throw "Grab gate failure: no downloaded files found under '$downloadDir' within ${GrabTimeoutSeconds}s."
                }

                Write-Host "✓ download artifacts: $($file.FullName)" -ForegroundColor Green
            }
        }
    }

    if ($RunTelemetryDIGate) {
        Write-Host "`n=== Telemetry DI gate: verify IDownloadTelemetryService resolution ===" -ForegroundColor Cyan

        # The telemetry gate checks that IDownloadTelemetryService from Common was actually
        # resolved and invoked during the download operation - not just that reflection can
        # see the type.
        #
        # DUAL-SIGNAL APPROACH (see docs/TELEMETRY_DI_CONTRACT.md):
        #   1. Primary: Look for structured JSON marker [LPC_TELEMETRY] {...}
        #      - Deterministic, machine-parseable, unlikely to change
        #   2. Fallback: Look for human-readable log patterns (regex-based)
        #      - Less stable but covers older plugin versions
        #
        # The structured marker is emitted at Debug level by DownloadTelemetryService.

        # Primary signal: Structured JSON marker
        # Format: [LPC_TELEMETRY] {"event":"telemetry_emitted","service":"...","track":"...","success":true/false}
        $structuredMarkerPrefix = "[LPC_TELEMETRY]"
        $structuredMarkerPattern = '\[LPC_TELEMETRY\]\s*\{[^}]*"event"\s*:\s*"telemetry_emitted"[^}]*\}'

        # Fallback signal: Human-readable log patterns (existing behavior)
        $fallbackPatterns = @(
            # Successful download telemetry (from DownloadTelemetryService.LogDownloadTelemetry)
            "Download completed:.*track=.*bytes=.*elapsed=.*rate=",
            # Failed download telemetry (also proves DI worked)
            "Download failed:.*track=.*elapsed=.*retries="
        )

        $telemetryFound = $false
        $telemetryLogLines = @()
        $telemetryStart = Get-Date
        $signalMethod = $null

        Write-Host "Checking Lidarr container logs for telemetry entries..." -ForegroundColor DarkGray
        Write-Host "  Primary signal: Structured JSON marker ($structuredMarkerPrefix)" -ForegroundColor DarkGray
        Write-Host "  Fallback signal: Human-readable log patterns (regex)" -ForegroundColor DarkGray

        while (((Get-Date) - $telemetryStart).TotalSeconds -lt $TelemetryGateTimeoutSeconds) {
            # Fetch recent container logs
            $logs = & docker logs $ContainerName --tail 1000 2>&1

            if ($LASTEXITCODE -eq 0 -and $logs) {
                $logText = $logs -join "`n"

                # PRIMARY: Check for structured JSON marker first
                if (-not $telemetryFound) {
                    $structuredMatches = [regex]::Matches($logText, $structuredMarkerPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                    if ($structuredMatches.Count -gt 0) {
                        $telemetryFound = $true
                        $signalMethod = "structured"
                        foreach ($m in $structuredMatches) {
                            # Extract the full line containing the match
                            $lineStart = $logText.LastIndexOf("`n", [Math]::Max(0, $m.Index - 1)) + 1
                            $lineEnd = $logText.IndexOf("`n", $m.Index)
                            if ($lineEnd -lt 0) { $lineEnd = $logText.Length }
                            $fullLine = $logText.Substring($lineStart, $lineEnd - $lineStart).Trim()
                            if ($fullLine -and $telemetryLogLines -notcontains $fullLine) {
                                $telemetryLogLines += $fullLine
                            }
                        }
                    }
                }

                # FALLBACK: Check for human-readable log patterns if structured not found
                if (-not $telemetryFound) {
                    foreach ($pattern in $fallbackPatterns) {
                        $fallbackMatches = [regex]::Matches($logText, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                        if ($fallbackMatches.Count -gt 0) {
                            $telemetryFound = $true
                            $signalMethod = "fallback-regex"
                            foreach ($m in $fallbackMatches) {
                                # Extract the full line containing the match
                                $lineStart = $logText.LastIndexOf("`n", [Math]::Max(0, $m.Index - 1)) + 1
                                $lineEnd = $logText.IndexOf("`n", $m.Index)
                                if ($lineEnd -lt 0) { $lineEnd = $logText.Length }
                                $fullLine = $logText.Substring($lineStart, $lineEnd - $lineStart).Trim()
                                if ($fullLine -and $telemetryLogLines -notcontains $fullLine) {
                                    $telemetryLogLines += $fullLine
                                }
                            }
                        }
                    }
                }

                if ($telemetryFound) { break }
            }

            Start-Sleep -Seconds 3
        }

        if (-not $telemetryFound) {
            Write-Host "✗ telemetry DI gate: no telemetry signals found within ${TelemetryGateTimeoutSeconds}s." -ForegroundColor Red
            Write-Host "  Primary signal (structured JSON) not found:" -ForegroundColor Yellow
            Write-Host "    Pattern: $structuredMarkerPattern" -ForegroundColor Yellow
            Write-Host "  Fallback signal (regex patterns) not found:" -ForegroundColor Yellow
            foreach ($pattern in $fallbackPatterns) {
                Write-Host "    - $pattern" -ForegroundColor Yellow
            }
            Write-Host "`n  This indicates IDownloadTelemetryService may not have been resolved or invoked." -ForegroundColor Yellow
            Write-Host "  Possible causes:" -ForegroundColor Yellow
            Write-Host "    1. DI registration missing in merged/internalized plugin assembly" -ForegroundColor Yellow
            Write-Host "    2. Type identity mismatch preventing interface resolution" -ForegroundColor Yellow
            Write-Host "    3. Download path did not invoke the telemetry service" -ForegroundColor Yellow
            Write-Host "    4. Log level set too high (structured marker requires Debug level)" -ForegroundColor Yellow

            # Show recent log tail for debugging
            Write-Host "`n  Recent container logs (last 50 lines):" -ForegroundColor Yellow
            $recentLogs = & docker logs $ContainerName --tail 50 2>&1
            if ($recentLogs) {
                $recentLogs | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            }

            throw "Telemetry DI gate failure: IDownloadTelemetryService was not invoked during download."
        }

        # Report which signal method succeeded
        $signalMethodDisplay = switch ($signalMethod) {
            "structured" { "structured JSON marker (primary)" }
            "fallback-regex" { "human-readable log regex (fallback)" }
            default { "unknown" }
        }
        Write-Host "✓ telemetry DI gate: found $($telemetryLogLines.Count) telemetry entry(ies) via $signalMethodDisplay" -ForegroundColor Green

        foreach ($line in $telemetryLogLines | Select-Object -First 5) {
            # Truncate long lines for display
            $display = if ($line.Length -gt 200) { $line.Substring(0, 200) + "..." } else { $line }
            Write-Host "  $display" -ForegroundColor DarkGray
        }

        if ($telemetryLogLines.Count -gt 5) {
            Write-Host "  ... and $($telemetryLogLines.Count - 5) more" -ForegroundColor DarkGray
        }

        Write-Host "✓ telemetry DI gate: IDownloadTelemetryService successfully resolved and invoked" -ForegroundColor Green
    }

    if ($RunGoldenPersistGate) {
        Write-Host "`n=== Golden-Persist gate: restart + revalidate ===" -ForegroundColor Cyan

        # Build expected implementations map for schema verification
        $expectedImplementations = @{
            indexer = @()
            downloadclient = @()
            importlist = @()
        }
        foreach ($plugin in $pluginNames) {
            if (-not $expectations.ContainsKey($plugin)) { continue }
            $exp = $expectations[$plugin]
            $expectedImplementations.indexer += $exp.Indexers
            $expectedImplementations.downloadclient += $exp.DownloadClients
            $expectedImplementations.importlist += $exp.ImportLists
        }

        # Get current queue count for duplicate detection
        $currentQueueCount = 0
        try {
            $queue = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/queue?page=1&pageSize=100" -Headers $headers -TimeoutSeconds 30
            $records = Get-LidarrQueueRecords -QueueResponse $queue
            $currentQueueCount = $records.Count
            Write-Host "Pre-restart queue count: $currentQueueCount" -ForegroundColor DarkGray
        }
        catch {
            Write-Host "Warning: Could not get pre-restart queue count: $($_.Exception.Message)" -ForegroundColor Yellow
        }

        # Get album ID from search gate if available
        $albumId = 0
        if ($searchGateAlbum -and $searchGateAlbum.id) {
            $albumId = $searchGateAlbum.id
        }

        # Run the golden-persist gate
        $persistResult = Invoke-GoldenPersistGate `
            -ContainerName $ContainerName `
            -LidarrUrl $lidarrUrl `
            -Headers $headers `
            -ExpectedImplementations $expectedImplementations `
            -AlbumId $albumId `
            -OriginalQueueCount $currentQueueCount `
            -StartupTimeoutSeconds $GoldenPersistStartupTimeoutSeconds `
            -VerifyTelemetry:$RunTelemetryDIGate

        if (-not $persistResult.Success) {
            Write-Host "✗ golden-persist gate failed: $($persistResult.Error)" -ForegroundColor Red
            foreach ($step in $persistResult.Steps) {
                $stepStatus = if ($step.Success) { "✓" } else { "✗" }
                Write-Host "  $stepStatus $($step.Step)" -ForegroundColor $(if ($step.Success) { "Green" } else { "Red" })
            }
            exit 1
        }

        Write-Host "✓ golden-persist gate: all persistence checks passed" -ForegroundColor Green
    }

    if ($RunAuthFailRedactionGate) {
        Write-Host "`n=== AuthFail-Redaction gate: auth failure + log redaction ===" -ForegroundColor Cyan

        $authFailPassed = $true
        $authFailTests = 0

        foreach ($plugin in $pluginNames) {
            if (-not $expectations.ContainsKey($plugin)) { continue }
            $exp = $expectations[$plugin]

            foreach ($impl in $exp.Indexers) {
                $badCreds = Get-BadCredentialPreset -Implementation $impl
                if (-not $badCreds) {
                    Write-Host "↷ authfail gate: no bad credential preset for $impl (skipping)" -ForegroundColor Yellow
                    continue
                }

                Write-Host "`nTesting auth failure + redaction for $impl..." -ForegroundColor Cyan
                $authFailTests++

                $authResult = Invoke-AuthFailRedactionGate `
                    -ContainerName $ContainerName `
                    -LidarrUrl $lidarrUrl `
                    -Headers $headers `
                    -Implementation $impl `
                    -BadCredentials $badCreds

                if (-not $authResult.Success) {
                    Write-Host "✗ authfail gate ($impl): $($authResult.Error)" -ForegroundColor Red
                    foreach ($step in $authResult.Steps) {
                        $stepStatus = if ($step.Success) { "✓" } else { "✗" }
                        Write-Host "  $stepStatus $($step.Step)" -ForegroundColor $(if ($step.Success) { "Green" } else { "Red" })
                    }
                    $authFailPassed = $false
                }
                else {
                    Write-Host "✓ authfail gate ($impl): auth failure handled correctly, logs redacted" -ForegroundColor Green
                }
            }
        }

        if ($authFailTests -eq 0) {
            Write-Host "↷ authfail gate: no supported implementations to test" -ForegroundColor Yellow
        }
        elseif (-not $authFailPassed) {
            exit 1
        }

        Write-Host "✓ authfail-redaction gate: all tests passed" -ForegroundColor Green
    }

    # Drift Sentinel gate: validate stub-vs-live field expectations
    # This runs OUTSIDE the Docker container - it probes live APIs directly
    if ($RunDriftSentinelGate) {
        Write-Host "`n=== Drift Sentinel gate: stub-vs-live field validation ===" -ForegroundColor Cyan

        # Determine which providers to check based on plugins
        $driftProviders = @()
        foreach ($plugin in $pluginNames) {
            switch ($plugin.ToLower()) {
                "qobuzarr" { if ("qobuz" -notin $driftProviders) { $driftProviders += "qobuz" } }
                "tidalarr" { if ("tidal" -notin $driftProviders) { $driftProviders += "tidal" } }
            }
        }

        if ($driftProviders.Count -eq 0) {
            Write-Host "↷ drift sentinel: no supported providers to check" -ForegroundColor Yellow
        }
        else {
            # Build credentials from environment variables for success mode
            $driftCredentials = @{}
            if ($DriftSentinelIncludeSuccessMode) {
                # Qobuz credentials
                $qobuzAppId = $env:QOBUZARR_APP_ID ?? $env:QOBUZ_APP_ID
                $qobuzAuthToken = $env:QOBUZARR_AUTH_TOKEN ?? $env:QOBUZ_AUTH_TOKEN
                if ($qobuzAppId) {
                    $driftCredentials["qobuz"] = @{
                        AppId = $qobuzAppId
                        AuthToken = $qobuzAuthToken
                    }
                }

                # Tidal - try OAuth client credentials flow, fall back to manual token
                $tidalClientId = $env:TIDALARR_CLIENT_ID ?? $env:TIDAL_CLIENT_ID
                $tidalClientSecret = $env:TIDALARR_CLIENT_SECRET ?? $env:TIDAL_CLIENT_SECRET
                $tidalAccessToken = $env:TIDALARR_ACCESS_TOKEN ?? $env:TIDAL_ACCESS_TOKEN
                $tidalMarket = $env:TIDALARR_MARKET ?? $env:TIDAL_MARKET ?? "US"

                if ($tidalClientId -and $tidalClientSecret) {
                    # Acquire token via OAuth client credentials flow
                    Write-Host "  Acquiring Tidal access token via OAuth..." -ForegroundColor DarkGray
                    $tokenResult = Get-TidalAccessToken -ClientId $tidalClientId -ClientSecret $tidalClientSecret
                    if ($tokenResult.Success) {
                        Write-Host "  Tidal token acquired (expires in $($tokenResult.ExpiresIn)s)" -ForegroundColor DarkGray
                        $driftCredentials["tidal"] = @{
                            AccessToken = $tokenResult.AccessToken
                            Market = $tidalMarket
                        }
                    }
                    else {
                        Write-Host "  Tidal OAuth failed: $($tokenResult.Error)" -ForegroundColor Yellow
                    }
                }
                elseif ($tidalAccessToken) {
                    # Fall back to manually provided token
                    $driftCredentials["tidal"] = @{
                        AccessToken = $tidalAccessToken
                        Market = $tidalMarket
                    }
                }

                if ($driftCredentials.Count -eq 0) {
                    Write-Host "  Note: Success mode enabled but no credentials found in environment" -ForegroundColor DarkGray
                }
            }

            # Write JSON artifact for triage/trending
            $driftArtifactPath = Join-Path $WorkRoot "artifacts/e2e/drift-sentinel.json"

            $driftResult = Invoke-DriftSentinel `
                -Providers $driftProviders `
                -FailOnDrift:$DriftSentinelFailOnDrift `
                -Credentials $driftCredentials `
                -IncludeSuccessMode:$DriftSentinelIncludeSuccessMode `
                -TimeoutSeconds 30 `
                -ArtifactPath $driftArtifactPath

            if (-not $driftResult.Success) {
                Write-Host "✗ drift sentinel gate failed" -ForegroundColor Red
                exit 1
            }

            if ($driftResult.DriftCount -gt 0) {
                Write-Host "⚠ drift sentinel: $($driftResult.DriftCount) drift(s) detected (warning mode)" -ForegroundColor Yellow
            }
            else {
                Write-Host "✓ drift sentinel gate: no drift detected" -ForegroundColor Green
            }
        }
    }

    Write-Host "`n Multi-plugin schema smoke test passed." -ForegroundColor Green
}
finally {
    Cleanup
}
