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
  pwsh scripts/multi-plugin-coexistence-proof.ps1 -LidarrTag nightly-3.1.3.4970
#>
[CmdletBinding()]
param(
    [string]$PluginsDir,
    [string]$LidarrTag = 'nightly-3.1.3.4970',
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
        # applemusicarr keeps the standard SDK output layout (Release/net8.0/...),
        # unlike tidalarr/qobuzarr which set AppendTargetFrameworkToOutputPath=false.
        DllCandidates = @(
            # Post-rename (May 2026): applemusicarr DLL filename now matches Lidarr's PluginLoader
            # glob "Lidarr.Plugin.*.dll" (NzbDrone.Common/Extensions/PathExtensions.cs:334).
            # Before this, the plugin loaded silently into nothing.
            'src/AppleMusicarr.Plugin/bin/Release/net8.0/Lidarr.Plugin.AppleMusicarr.dll',
            'src/AppleMusicarr.Plugin/bin/Debug/net8.0/Lidarr.Plugin.AppleMusicarr.dll'
        )
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

# Verify all DLLs exist post-build. Plugins use either a flat `bin/` layout
# (tidalarr/qobuzarr/brainarr — AppendTargetFrameworkToOutputPath=false) or
# the standard SDK Release/net8.0 nested layout (applemusicarr). Try the
# explicit `Dll` path first, then any `DllCandidates`.
$dllPaths = @{}
foreach ($p in $plugins) {
    $repoDir = Join-Path $PluginsDir $p.Repo
    $candidates = @()
    if ($p.ContainsKey('Dll'))           { $candidates += (Join-Path $repoDir $p.Dll) }
    if ($p.ContainsKey('DllCandidates')) { $candidates += $p.DllCandidates | ForEach-Object { Join-Path $repoDir $_ } }

    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $found) {
        Write-Error "Plugin DLL not found for $($p.Repo). Tried:`n - $($candidates -join "`n - ")"
        exit 3
    }
    $dllPaths[$p.Repo] = (Resolve-Path -LiteralPath $found).Path
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
    # Use `docker exec curl` to probe Lidarr from inside the container, not from
    # the host. Reasons:
    #   1. On Docker Desktop for Windows, the host→container port forward can
    #      have multi-second latency or hang on the first connect after startup.
    #      The probe inside the container is sub-millisecond local-loopback.
    #   2. We don't need host reachability for the proof — we only need to know
    #      whether each plugin appears in Lidarr's schema endpoints from the
    #      Lidarr process's perspective.
    Write-Host "[wait] Lidarr startup (timeout ${StartupTimeoutSeconds}s)" -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $apiKey = $null
    while ((Get-Date) -lt $deadline) {
        $initJson = docker exec $ContainerName curl -fsS http://localhost:8686/initialize.json 2>$null
        if ($LASTEXITCODE -eq 0 -and $initJson) {
            try {
                $initObj = $initJson | ConvertFrom-Json
                if ($initObj.apiKey) {
                    docker exec $ContainerName curl -fsS -H "X-Api-Key: $($initObj.apiKey)" "http://localhost:8686/api/v1/system/status" 2>$null | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        $apiKey = $initObj.apiKey
                        break
                    }
                }
            } catch { }
        }
        Start-Sleep -Seconds 2
    }
    if (-not $apiKey) {
        Write-Error "Lidarr did not become healthy within ${StartupTimeoutSeconds}s"
        Write-Host "==== Container logs (last 200 lines) ====" -ForegroundColor Yellow
        docker logs --tail 200 $ContainerName | Out-Host
        exit 5
    }
    Write-Host "[ok] Lidarr healthy; apiKey acquired" -ForegroundColor Green

    # ── Assert each plugin appears in expected schema endpoints ────
    # Probe via `docker exec curl` for the same reason as the health probe
    # (consistent semantics, avoids host port-forward hangs).
    $failures = @()
    foreach ($p in $plugins) {
        foreach ($schema in $p.Schemas) {
            $body = docker exec $ContainerName curl -fsS -H "X-Api-Key: $apiKey" "http://localhost:8686/api/v1/$schema/schema" 2>$null
            if ($LASTEXITCODE -ne 0 -or -not $body) {
                $failures += "$($p.Repo) /$schema/schema fetch failed (curl exit $LASTEXITCODE)"
                continue
            }
            # Match `"name": "...<substring>..."` or `"implementation": "...<substring>..."`
            # in the raw JSON. Substring match is intentional — it survives both legacy
            # name conventions ("Tidalarr", "Tidal") and casing variations.
            $pattern = "(?i)""(name|implementation)""\s*:\s*""[^""]*$([regex]::Escape($p.Substring))[^""]*"""
            $matches = [regex]::Matches($body, $pattern)
            if ($matches.Count -gt 0) {
                Write-Host "[ok] $($p.Repo) ✓ /api/v1/$schema/schema ($($matches.Count) matches for '$($p.Substring)')" -ForegroundColor Green
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
