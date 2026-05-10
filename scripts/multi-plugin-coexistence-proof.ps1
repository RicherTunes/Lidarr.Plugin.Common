<#
.SYNOPSIS
  Proof harness for multi-plugin co-existence in a single Lidarr host.

.DESCRIPTION
  Mounts the merged plugin DLLs of tidalarr / qobuzarr / brainarr / applemusicarr
  into ONE Lidarr container simultaneously, waits for the API to become healthy,
  then asserts each plugin's expected entry appears in the corresponding schema
  endpoint(s). Exits 0 on full success, non-zero on any failure with diagnostics
  + container logs.

  This is the directly-runnable version of the `Multi-Plugin Smoke Test`
  GitHub workflow. Use it to verify locally that the fixes in PR #483
  (HttpClient metrics-filter suppression, Azure reflection-loading) actually
  unblock multi-plugin loading — independent of the upstream Lidarr ALC
  lifecycle bug documented in docs/ECOSYSTEM_PARITY_ROADMAP.md.

.PARAMETER PluginsDir
  Parent directory containing tidalarr/, qobuzarr/, brainarr/, applemusicarr/
  sibling repos. Defaults to the parent of this script's repo.

.PARAMETER LidarrTag
  Lidarr Docker image tag. Must be a .NET 8 plugins-branch build.

.PARAMETER ContainerName
  Container name. Default 'multi-plugin-coexistence'.

.PARAMETER HostPort
  Host port that Lidarr 8686 binds to. Default 8688 (avoids per-plugin
  single-instance ports 8690-8692).

.PARAMETER StartupTimeoutSeconds
  Seconds to wait for Lidarr to become healthy after `docker run`.

.PARAMETER SkipBuild
  Don't rebuild plugin DLLs — assume they already exist at the expected paths.

.EXAMPLE
  pwsh scripts/multi-plugin-coexistence-proof.ps1
  pwsh scripts/multi-plugin-coexistence-proof.ps1 -SkipBuild
  pwsh scripts/multi-plugin-coexistence-proof.ps1 -LidarrTag pr-plugins-3.1.2.4913
#>
[CmdletBinding()]
param(
    [string]$PluginsDir,
    [string]$LidarrTag = 'pr-plugins-3.1.2.4913',
    [string]$ContainerName = 'multi-plugin-coexistence',
    [int]$HostPort = 8688,
    [int]$StartupTimeoutSeconds = 120,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Resolve paths ───────────────────────────────────────────────────
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
if (-not $PluginsDir) { $PluginsDir = Split-Path -Parent $repoRoot }
$PluginsDir = (Resolve-Path -LiteralPath $PluginsDir).Path

# ── Plugin manifest ─────────────────────────────────────────────────
# (Owner, RepoDir, MergedDllRelative, MountPath, ExpectedSchemaSubstring,
#  Schemas[]) — Schemas is the list of /api/v1/{name}/schema endpoints the
# plugin should appear in.
$plugins = @(
    @{
        Repo = 'tidalarr'
        Dll  = 'src/Tidalarr/bin/Lidarr.Plugin.Tidalarr.dll'
        Mount = '/config/plugins/RicherTunes/Tidalarr'
        Substring = 'Tidal'
        Schemas = @('indexer', 'downloadclient')
        BuildCmd = { param($d) dotnet build "$d/src/Tidalarr/Tidalarr.csproj" -c Release -m:1 }
    },
    @{
        Repo = 'qobuzarr'
        Dll  = 'bin/Lidarr.Plugin.Qobuzarr.dll'
        Mount = '/config/plugins/RicherTunes/Qobuzarr'
        Substring = 'Qobuz'
        Schemas = @('indexer', 'downloadclient')
        BuildCmd = { param($d) dotnet build "$d/Qobuzarr.csproj" -c Release -m:1 }
    },
    @{
        Repo = 'brainarr'
        Dll  = 'Brainarr.Plugin/bin/Lidarr.Plugin.Brainarr.dll'
        Mount = '/config/plugins/RicherTunes/Brainarr'
        Substring = 'Brainarr'
        Schemas = @('importlist')
        BuildCmd = { param($d) dotnet build "$d/Brainarr.Plugin/Brainarr.Plugin.csproj" -c Release -m:1 }
    },
    @{
        Repo = 'applemusicarr'
        Dll  = 'src/AppleMusicarr.Plugin/bin/AppleMusicarr.Plugin.dll'
        Mount = '/config/plugins/RicherTunes/AppleMusicarr'
        Substring = 'AppleMusic'
        Schemas = @('indexer', 'downloadclient')
        BuildCmd = { param($d) dotnet build "$d/src/AppleMusicarr.Plugin/AppleMusicarr.Plugin.csproj" -c Release -m:1 }
    }
)

# ── Preflight ───────────────────────────────────────────────────────
$missing = @()
foreach ($p in $plugins) {
    $repoPath = Join-Path $PluginsDir $p.Repo
    if (-not (Test-Path $repoPath -PathType Container)) {
        $missing += "$($p.Repo) (looked for $repoPath)"
    }
}
if ($missing.Count -gt 0) {
    Write-Error "Missing plugin repos:`n - $($missing -join "`n - ")"
    exit 2
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error "docker not on PATH"
    exit 2
}
docker info 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker engine not running. Start Docker Desktop and retry."
    exit 2
}

# ── Build plugins ───────────────────────────────────────────────────
if (-not $SkipBuild) {
    foreach ($p in $plugins) {
        $repoPath = Join-Path $PluginsDir $p.Repo
        Write-Host "[build] $($p.Repo)" -ForegroundColor Cyan
        & $p.BuildCmd $repoPath
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Build failed for $($p.Repo)"
            exit 3
        }
    }
}

# Verify all DLLs exist post-build
$dllPaths = @{}
foreach ($p in $plugins) {
    $abs = Join-Path (Join-Path $PluginsDir $p.Repo) $p.Dll
    if (-not (Test-Path -LiteralPath $abs)) {
        Write-Error "Plugin DLL not found: $abs (build with -SkipBuild=`$false or build manually)"
        exit 3
    }
    $dllPaths[$p.Repo] = (Resolve-Path -LiteralPath $abs).Path
}

# ── Tear down any prior container ──────────────────────────────────
docker rm -f $ContainerName 2>&1 | Out-Null

# ── Spin up Lidarr with ALL plugins mounted ────────────────────────
$mountArgs = @()
foreach ($p in $plugins) {
    $pluginDir = Split-Path -Parent $dllPaths[$p.Repo]
    # Quote both sides for paths with spaces; container path is fixed shape so no quote needed.
    $mountArgs += "-v"
    $mountArgs += "${pluginDir}:$($p.Mount):ro"
}

Write-Host "[docker] starting $ContainerName from ghcr.io/hotio/lidarr:$LidarrTag with $($plugins.Count) plugins mounted" -ForegroundColor Cyan
$dockerArgs = @('run', '-d', '--name', $ContainerName, '-p', "${HostPort}:8686") + $mountArgs + @("ghcr.io/hotio/lidarr:$LidarrTag")
& docker @dockerArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "docker run failed"
    exit 4
}

try {
    # ── Wait for Lidarr to become healthy ──────────────────────────
    $baseUrl = "http://localhost:$HostPort"
    Write-Host "[wait] Lidarr startup at $baseUrl (timeout ${StartupTimeoutSeconds}s)" -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $apiKey = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $initJson = (docker exec $ContainerName cat /config/initialize.json 2>$null)
            if ($LASTEXITCODE -eq 0 -and $initJson) {
                $obj = $initJson | ConvertFrom-Json
                if ($obj.PSObject.Properties['apiKey'] -and $obj.apiKey) {
                    $apiKey = $obj.apiKey
                    break
                }
            }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $apiKey) {
        Write-Error "Lidarr did not become healthy within ${StartupTimeoutSeconds}s"
        Write-Host "==== Container logs ====" -ForegroundColor Yellow
        docker logs $ContainerName | Out-Host
        exit 5
    }
    Write-Host "[ok] Lidarr healthy; apiKey acquired" -ForegroundColor Green

    # ── Assert each plugin appears in expected schema endpoints ────
    $headers = @{ 'X-Api-Key' = $apiKey }
    $failures = @()

    foreach ($p in $plugins) {
        foreach ($schema in $p.Schemas) {
            $url = "$baseUrl/api/v1/$schema/schema"
            try {
                $body = Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 10 -ErrorAction Stop
            } catch {
                $failures += "$($p.Repo) /$schema/schema fetch failed: $($_.Exception.Message)"
                continue
            }
            $hits = @($body | Where-Object {
                ($_.name -and ($_.name -like "*$($p.Substring)*")) -or
                ($_.implementation -and ($_.implementation -like "*$($p.Substring)*"))
            })
            if ($hits.Count -gt 0) {
                Write-Host "[ok] $($p.Repo) ✓ /api/v1/$schema/schema (matched '$($p.Substring)' in $($hits.Count) entries)" -ForegroundColor Green
            } else {
                $failures += "$($p.Repo) NOT FOUND in /api/v1/$schema/schema (looked for '$($p.Substring)')"
            }
        }
    }

    if ($failures.Count -gt 0) {
        Write-Host ""
        Write-Host "==== FAILURES ====" -ForegroundColor Red
        foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
        Write-Host ""
        Write-Host "==== Container logs (last 200 lines) ====" -ForegroundColor Yellow
        docker logs --tail 200 $ContainerName | Out-Host
        exit 6
    }

    Write-Host ""
    Write-Host "==== ALL $($plugins.Count) PLUGINS CO-EXIST CLEANLY ====" -ForegroundColor Green
    Write-Host "Lidarr loaded each plugin without conflict." -ForegroundColor Green
    exit 0
}
finally {
    Write-Host ""
    Write-Host "[cleanup] removing $ContainerName" -ForegroundColor DarkGray
    docker rm -f $ContainerName 2>&1 | Out-Null
}
