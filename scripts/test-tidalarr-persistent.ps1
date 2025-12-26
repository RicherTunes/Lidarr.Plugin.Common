#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Quick test script for Tidalarr with persistent Lidarr config.

.DESCRIPTION
    Runs Lidarr with the Tidalarr plugin, preserving config between runs.
    First run: Complete the OAuth PKCE flow and paste RedirectUrl in the UI.
    Subsequent runs: Config is preserved; rebuild/redeploy the plugin as needed.

.PARAMETER Rebuild
    Rebuild the plugin before starting.

.PARAMETER Clean
    Delete persistent config and start fresh.

.PARAMETER LidarrTag
    Lidarr Docker tag. Default: pr-plugins-3.1.1.4884

.PARAMETER Port
    Host port to bind Lidarr to. Default: 8690

.PARAMETER ContainerName
    Docker container name. Default: tidalarr-test
#>
param(
    [switch]$Rebuild,
    [switch]$Clean,
    [string]$LidarrTag = "pr-plugins-3.1.1.4884",
    [int]$Port = 8690,
    [string]$ContainerName = "tidalarr-test",
    [switch]$SkipSchemaCheck,
    [switch]$RunSearchGate,
    [switch]$RunGrabGate,
    [int]$SearchTimeoutSeconds = 180,
    [int]$GrabTimeoutSeconds = 300,
    [string]$SearchArtistTerm = "Miles Davis",
    [string]$SearchAlbumTitle = "Kind of Blue",
    [switch]$RequireDownloadedFiles
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$commonRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $commonRoot
$tidalarrRoot = Join-Path $repoRoot "tidalarr"

if (-not (Test-Path (Join-Path $tidalarrRoot "Tidalarr.sln"))) {
    throw "Tidalarr repo not found at '$tidalarrRoot'."
}

function UrlEncode([string]$value) {
    if ($null -eq $value) { return "" }
    return [System.Web.HttpUtility]::UrlEncode($value)
}

function Invoke-LidarrApiJson {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter()][object]$Body = $null,
        [Parameter()][int]$TimeoutSeconds = 30
    )

    $params = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
        TimeoutSec = $TimeoutSeconds
        ErrorAction = "Stop"
    }

    if ($null -ne $Body) {
        $params.ContentType = "application/json"
        $params.Body = ($Body | ConvertTo-Json -Depth 64)
    }

    return Invoke-RestMethod @params
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
        if ($cmd -and $cmd.status) {
            if ($cmd.status -eq "completed") { return $cmd }
            if ($cmd.status -eq "failed") { throw "Command $CommandId failed: $($cmd.message)" }
        }
        Start-Sleep -Seconds 3
    }

    throw "Timeout waiting for command $CommandId to complete."
}

function Wait-DirectoryHasAnyFiles {
    param(
        [Parameter(Mandatory = $true)][string]$DirectoryPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $start = Get-Date
    while (((Get-Date) - $start).TotalSeconds -lt $TimeoutSeconds) {
        if (Test-Path $DirectoryPath) {
            $file = Get-ChildItem -LiteralPath $DirectoryPath -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.Length -gt 0 } |
                Sort-Object LastWriteTimeUtc -Descending |
                Select-Object -First 1

            if ($file) { return $file }
        }

        Start-Sleep -Seconds 5
    }

    return $null
}

function Find-LatestZip {
    param([Parameter(Mandatory = $true)][string]$Directory)

    if (-not (Test-Path $Directory)) { return $null }
    return (Get-ChildItem -LiteralPath $Directory -Filter *.zip -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1)
}

$persistentRoot = Join-Path $commonRoot ".persistent-tidalarr-test-config"      
$configDir = Join-Path $persistentRoot "config"
$pluginsDir = Join-Path $persistentRoot "plugins/RicherTunes/Tidalarr"
$downloadsDir = Join-Path $persistentRoot "downloads"
$musicDir = Join-Path $persistentRoot "music"

if ($Clean) {
    Write-Host "Cleaning persistent config..." -ForegroundColor Yellow
    Remove-Item -Path $persistentRoot -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $configDir -Force | Out-Null
New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null
New-Item -ItemType Directory -Path $downloadsDir -Force | Out-Null
New-Item -ItemType Directory -Path $musicDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $downloadsDir "tidalarr") -Force | Out-Null

if ($Rebuild) {
    Write-Host "Building Tidalarr package..." -ForegroundColor Cyan
    Push-Location $tidalarrRoot
    try {
        & pwsh -File ".\\build.ps1" -Package -Configuration Release | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Tidalarr build.ps1 -Package failed." }
    }
    finally {
        Pop-Location
    }
}

$zip = Find-LatestZip -Directory (Join-Path $tidalarrRoot "src/Tidalarr/artifacts/packages")
if (-not $zip) {
    throw "No Tidalarr package zip found under 'tidalarr/src/Tidalarr/artifacts/packages'. Run with -Rebuild."
}

Write-Host "Tidalarr zip: $($zip.FullName)" -ForegroundColor Gray

Write-Host "Extracting plugin to persistent directory..." -ForegroundColor Cyan
Remove-Item -Path (Join-Path $pluginsDir "*") -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $zip.FullName -DestinationPath $pluginsDir -Force

$requiredFiles = @(
    "plugin.json",
    "Lidarr.Plugin.Tidalarr.dll",
    "Lidarr.Plugin.Abstractions.dll"
)
$missing = @($requiredFiles | Where-Object { -not (Test-Path (Join-Path $pluginsDir $_)) })
if ($missing.Count -gt 0) {
    Write-Warning "Plugin zip is missing required files: $($missing -join ', ')"
    Write-Host "Extracted files:" -ForegroundColor Yellow
    Get-ChildItem -LiteralPath $pluginsDir -File | Select-Object Name, Length | Format-Table -AutoSize | Out-Host
    throw "Tidalarr plugin package is incomplete; cannot start Lidarr."
}

docker stop $ContainerName 2>$null | Out-Null
docker rm $ContainerName 2>$null | Out-Null

$configMount = $configDir.Replace('\', '/')
$pluginsMount = (Split-Path $pluginsDir -Parent).Replace('\', '/')
$downloadsMount = $downloadsDir.Replace('\', '/')
$musicMount = $musicDir.Replace('\', '/')

Write-Host "Starting Lidarr with persistent config..." -ForegroundColor Cyan
docker run -d `
    --name $ContainerName `
    -p "${Port}:8686" `
    -v "${configMount}:/config" `
    -v "${pluginsMount}:/config/plugins/RicherTunes" `
    -v "${downloadsMount}:/downloads" `
    -v "${musicMount}:/music" `
    -e PUID=1000 `
    -e PGID=1000 `
    -e TZ=UTC `
    "ghcr.io/hotio/lidarr:$LidarrTag" | Out-Null

Write-Host "Waiting for Lidarr to start..." -ForegroundColor Yellow
$timeoutSeconds = 60
$start = Get-Date
$ready = $false
while (((Get-Date) - $start).TotalSeconds -lt $timeoutSeconds) {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$Port/api/v1/system/status" -TimeoutSec 5 -ErrorAction SilentlyContinue
        if ($response.StatusCode -eq 200) {
            $ready = $true
            break
        }
    }
    catch { }
    Start-Sleep -Seconds 2
}

if (-not $ready) {
    Write-Warning "Timed out waiting for Lidarr to respond on http://localhost:$Port. Container may still be starting."
    try {
        Write-Host "Recent container logs:" -ForegroundColor Yellow
        docker logs $ContainerName --tail 200 | Out-Host
    }
    catch { }
}

if (-not $SkipSchemaCheck) {
    $apiKey = $null
    $configXmlPath = Join-Path $configDir "config.xml"

    $apiKeyDeadline = (Get-Date).AddSeconds(30)
    while (-not $apiKey -and (Get-Date) -lt $apiKeyDeadline) {
        if (Test-Path $configXmlPath) {
            try {
                $xml = [xml](Get-Content -LiteralPath $configXmlPath -Raw)
                $apiKey = $xml.Config.ApiKey
            }
            catch { }
        }
        if (-not $apiKey) { Start-Sleep -Seconds 2 }
    }

    if ($apiKey) {
        try {
            $headers = @{ "X-Api-Key" = $apiKey }
            $lidarrUrl = "http://localhost:$Port"
            $indexers = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/indexer/schema" -Headers $headers -TimeoutSeconds 30
            $downloadClients = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/downloadclient/schema" -Headers $headers -TimeoutSeconds 30

            $hasTidalIndexer = @($indexers | Where-Object { ($_.implementation -like "*Tidal*") -or ($_.name -like "*Tidal*") }).Count -gt 0
            $hasTidalDownloadClient = @($downloadClients | Where-Object { ($_.implementation -like "*Tidal*") -or ($_.name -like "*Tidal*") }).Count -gt 0

            if (-not $hasTidalIndexer -or -not $hasTidalDownloadClient) {
                throw "Tidalarr schema not detected via API (indexer=$hasTidalIndexer downloadClient=$hasTidalDownloadClient)."
            }

            Write-Host "Schema check OK: Tidalarr indexer + download client detected." -ForegroundColor Green
        }
        catch {
            Write-Warning "Schema check failed: $($_.Exception.Message)"
            Write-Host "Tip: check container logs with: docker logs -f $ContainerName" -ForegroundColor Yellow
        }
    }
    else {
        Write-Warning "Could not read Lidarr API key from $configXmlPath; skipping schema check."
    }
}

if ($RunGrabGate) { $RunSearchGate = $true }

if ($RunSearchGate -or $RunGrabGate) {
    $lidarrUrl = "http://localhost:$Port"

    $configXmlPath = Join-Path $configDir "config.xml"
    if (-not (Test-Path $configXmlPath)) { throw "Cannot run gates: missing Lidarr config file at $configXmlPath." }

    $xml = [xml](Get-Content -LiteralPath $configXmlPath -Raw)
    $apiKey = $xml.Config.ApiKey
    if ([string]::IsNullOrWhiteSpace($apiKey)) { throw "Cannot run gates: Lidarr ApiKey missing from $configXmlPath." }

    $headers = @{ "X-Api-Key" = $apiKey }

    Write-Host "`n=== Gate: Search (AlbumSearch + releases) ===" -ForegroundColor Cyan

    $existingIndexers = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/indexer" -Headers $headers -TimeoutSeconds 30
    $tidalIndexer = $existingIndexers | Where-Object { $_.enable -eq $true -and $_.implementation -like "*Tidal*" } | Select-Object -First 1
    if (-not $tidalIndexer) { throw "Search gate requires an enabled Tidal indexer. Configure it in the UI and click Test." }

    $existingDownloadClients = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/downloadclient" -Headers $headers -TimeoutSeconds 30
    $tidalDownloadClient = $existingDownloadClients | Where-Object { $_.enable -eq $true -and $_.implementation -like "*Tidal*" } | Select-Object -First 1
    if (-not $tidalDownloadClient) { throw "Search gate expects an enabled Tidal download client (needed for grab gate). Configure it in the UI and click Test." }

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

    $artistPayload = $artistCandidate | ConvertTo-Json -Depth 64 | ConvertFrom-Json
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
    if (-not $cmd -or -not $cmd.id) { throw "Failed to enqueue AlbumSearch command." }
    $null = Wait-LidarrCommandCompleted -CommandId $cmd.id -LidarrUrl $lidarrUrl -Headers $headers -TimeoutSeconds $SearchTimeoutSeconds

    $releases = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/release?albumId=$($album.id)" -Headers $headers -TimeoutSeconds 30
    $releaseCount = 0
    if ($releases) { $releaseCount = $releases.Count }
    if ($releaseCount -eq 0) { throw "Search gate failure: release list is empty for albumId=$($album.id)." }

    $indexerNames = $releases | ForEach-Object { $_.indexer } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique
    Write-Host "Search gate releases: $releaseCount (indexers: $($indexerNames -join ', '))" -ForegroundColor Green

    if ($RunGrabGate) {
        Write-Host "`n=== Gate: Grab (queue download) ===" -ForegroundColor Cyan

        $candidates = $releases | Where-Object { $_.indexer -eq $tidalIndexer.name -and $_.downloadAllowed -ne $false }
        $release = $candidates | Sort-Object { if ($_.size) { [long]$_.size } else { [long]::MaxValue } } | Select-Object -First 1
        if (-not $release) {
            throw "Grab gate failure: no releases attributed to Tidal indexer '$($tidalIndexer.name)'. Consider using a different SearchArtistTerm/SearchAlbumTitle."
        }

        $payload = $release | ConvertTo-Json -Depth 64 | ConvertFrom-Json
        $payload | Add-Member -NotePropertyName "downloadClientId" -NotePropertyValue $tidalDownloadClient.id -Force

        $null = Invoke-LidarrApiJson -Method "POST" -Uri "$lidarrUrl/api/v1/release" -Headers $headers -Body $payload -TimeoutSeconds 60
        Write-Host "✓ queued '$($release.title)' via '$($tidalIndexer.name)' -> '$($tidalDownloadClient.name)'" -ForegroundColor Green

        $queueStart = Get-Date
        $queueOk = $false
        while (((Get-Date) - $queueStart).TotalSeconds -lt $GrabTimeoutSeconds) {
            $queue = Invoke-LidarrApiJson -Method "GET" -Uri "$lidarrUrl/api/v1/queue?page=1&pageSize=50" -Headers $headers -TimeoutSeconds 30
            $records = $queue.records
            if (-not $records) { $records = @() }

            $match = $records | Where-Object { $_.downloadClient -eq $tidalDownloadClient.name -and $_.albumId -eq $album.id } | Select-Object -First 1
            if (-not $match) {
                $match = $records | Where-Object { $_.downloadClient -eq $tidalDownloadClient.name -and $_.title -and $_.title.ToString().IndexOf($album.title, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | Select-Object -First 1
            }

            if ($match) {
                if ($match.status -eq "failed" -or -not [string]::IsNullOrWhiteSpace($match.errorMessage)) {
                    $err = $match.errorMessage
                    if ([string]::IsNullOrWhiteSpace($err)) { $err = "Queue item status='$($match.status)'." }
                    throw "Grab gate failure: queue item reported an error: $err"
                }

                Write-Host "✓ queue item observed (status=$($match.status))" -ForegroundColor Green
                $queueOk = $true
                break
            }

            Start-Sleep -Seconds 5
        }

        if (-not $queueOk) {
            throw "Grab gate failure: did not observe a queue item within ${GrabTimeoutSeconds}s."
        }

        if ($RequireDownloadedFiles) {
            $downloadDir = Join-Path $downloadsDir "tidalarr"
            Write-Host "Waiting for downloaded files under: $downloadDir" -ForegroundColor Yellow
            $file = Wait-DirectoryHasAnyFiles -DirectoryPath $downloadDir -TimeoutSeconds $GrabTimeoutSeconds
            if (-not $file) {
                throw "Grab gate failure: no downloaded files found under '$downloadDir' within ${GrabTimeoutSeconds}s."
            }
            Write-Host "✓ download artifacts: $($file.FullName)" -ForegroundColor Green
        }
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Lidarr is running at: http://localhost:$Port" -ForegroundColor Green
Write-Host "Config persisted at: $configDir" -ForegroundColor Green
Write-Host "Plugin persisted at: $pluginsDir" -ForegroundColor Green
Write-Host "Downloads persisted at: $downloadsDir" -ForegroundColor Green
Write-Host "Music persisted at: $musicDir" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Tidalarr OAuth setup (first run):" -ForegroundColor Yellow
Write-Host "1) Add Tidalarr indexer/download client in Lidarr UI" -ForegroundColor Yellow
Write-Host "2) Set ConfigPath to: /config/tidalarr" -ForegroundColor Yellow     
Write-Host "3) Click to generate the auth URL, login, then paste the RedirectUrl back" -ForegroundColor Yellow
Write-Host "4) Set TidalMarket (e.g., US) and click Test" -ForegroundColor Yellow
Write-Host ""
Write-Host "Recommended paths (for E2E):" -ForegroundColor Yellow
Write-Host "- Download folder in Lidarr / client settings: /downloads/tidalarr" -ForegroundColor Yellow
Write-Host "- Music root in Lidarr: /music" -ForegroundColor Yellow
Write-Host ""
Write-Host "To stop: docker stop $ContainerName" -ForegroundColor Gray
