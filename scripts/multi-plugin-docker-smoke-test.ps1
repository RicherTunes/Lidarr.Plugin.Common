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

.PARAMETER PluginZip
    One or more plugin zips in the form: name=path
    Example: qobuzarr=D:\repo\Qobuzarr-latest.zip

.PARAMETER PluginsOwner
    Owner folder under /config/plugins. Default: RicherTunes

.PARAMETER KeepRunning
    Do not stop/remove the container after the test.

.EXAMPLE
    .\scripts\multi-plugin-docker-smoke-test.ps1 `
      -PluginZip "qobuzarr=D:\Alex\github\qobuzarr\Qobuzarr-latest.zip" `
      -PluginZip "tidalarr=D:\Alex\github\tidalarr\Tidalarr-latest.zip"
#>

[CmdletBinding()]
param(
    [string]$LidarrTag = "pr-plugins-2.14.2.4786",
    [string]$ContainerName = "lidarr-multi-plugin-smoke",
    [int]$Port = 8689,
    [int]$StartupTimeoutSeconds = 120,
    [int]$SchemaTimeoutSeconds = 60,
    [string[]]$PluginZip = @(),
    [string]$PluginsOwner = "RicherTunes",
    [switch]$KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

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

    if (Test-Path $workRoot) {
        Remove-Item -Recurse -Force $workRoot
    }
    New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $configRoot | Out-Null

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

    $dockerArgs = @(
        "run", "-d",
        "--name", $ContainerName,
        "-p", "${Port}:8686",
        "-v", "${configMount}:/config",
        "-v", "${pluginMount}:/config/plugins:ro",
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

    Write-Host "`n🎉 Multi-plugin schema smoke test passed." -ForegroundColor Green
}
finally {
    Cleanup
}

