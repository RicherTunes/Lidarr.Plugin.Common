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

.PARAMETER GrabTimeoutSeconds
    Max time to wait for a grabbed release to appear in Lidarr's queue. Default: 300

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

function Normalize-PluginAbstractions {
    param([Parameter(Mandatory = $true)][string]$PluginsRoot)

    $abstractionDlls = @(Get-ChildItem -LiteralPath $PluginsRoot -Recurse -File -Filter 'Lidarr.Plugin.Abstractions.dll' -ErrorAction SilentlyContinue)
    if (-not $abstractionDlls -or $abstractionDlls.Count -eq 0) {
        throw "No Lidarr.Plugin.Abstractions.dll found under '$PluginsRoot'. Plugins must ship it (it is not present in the host image)."
    }

    if ($abstractionDlls.Count -eq 1) {
        return
    }

    $identities = $abstractionDlls | ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName
            FullName = [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName).FullName
        }
    }

    $uniqueIdentities = @($identities | Group-Object FullName)
    if ($uniqueIdentities.Count -gt 1) {
        $details = $uniqueIdentities | ForEach-Object {
            $paths = ($_.Group | Select-Object -ExpandProperty Path) -join "`n  - "
            "$($_.Name):`n  - $paths"
        } | Out-String

        throw "Multiple DIFFERENT Lidarr.Plugin.Abstractions identities detected. All plugins must reference the same Abstractions assembly identity to avoid type identity conflicts.`n$details"
    }

    $hashes = $abstractionDlls | ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName
            Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }

    $uniqueHashes = @($hashes | Group-Object Hash)
    if ($uniqueHashes.Count -gt 1) {
        Write-Host "Multiple Lidarr.Plugin.Abstractions.dll copies with the same identity but different bytes detected ($($abstractionDlls.Count)). This is usually OK (the host should unify by assembly identity), but consider standardizing how Abstractions is produced to reduce risk." -ForegroundColor Yellow
    } else {
        Write-Host "Multiple identical Lidarr.Plugin.Abstractions.dll copies detected ($($abstractionDlls.Count)); leaving as-is." -ForegroundColor Yellow
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

    # Always mount /music and /downloads for interactive setup and gate tests
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

            $candidates = $searchGateReleases | Where-Object { $_.indexer -eq $indexerName -and $_.downloadAllowed -ne $false }
            $release = $candidates | Sort-Object { if ($_.size) { [long]$_.size } else { [long]::MaxValue } } | Select-Object -First 1
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

    Write-Host "`n🎉 Multi-plugin schema smoke test passed." -ForegroundColor Green
}
finally {
    Cleanup
}
