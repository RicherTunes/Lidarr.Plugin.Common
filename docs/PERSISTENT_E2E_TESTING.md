# Persistent Local E2E Testing (Lidarr + Plugins)

This repo contains PowerShell scripts to run Lidarr in Docker while persisting config between runs.

## Qobuzarr only (single-plugin)

Script: `scripts/test-qobuzarr-persistent.ps1`

```powershell
# Start (uses cached build if present)
pwsh scripts/test-qobuzarr-persistent.ps1

# Force rebuild plugin zip before starting
pwsh scripts/test-qobuzarr-persistent.ps1 -Rebuild

# Wipe persisted config and start fresh
pwsh scripts/test-qobuzarr-persistent.ps1 -Clean
```

UI: `http://localhost:8689`

## Qobuzarr + Tidalarr together (multi-plugin, persistent)

Script: `scripts/multi-plugin-docker-smoke-test.ps1`

This script can run in a “persistent” mode so your Lidarr database/config survives container restarts.

```powershell
$q = (Resolve-Path ..\qobuzarr\artifacts\packages\qobuzarr-0.1.0-dev-net8.0.zip).Path
$t = (Resolve-Path ..\tidalarr\src\Tidalarr\artifacts\packages\tidalarr-1.0.1-net8.0.zip).Path

# Optional: mount a locally-built Lidarr DLL into the container to validate an upstream fix
$hostOverride = (Resolve-Path ..\_upstream\Lidarr\_output\net8.0\Lidarr.Common.dll).Path

pwsh scripts/multi-plugin-docker-smoke-test.ps1 `
  -PluginZip @("qobuzarr=$q","tidalarr=$t") `
  -ContainerName lidarr-multi-plugin-persist `
  -Port 8691 `
  -PreserveState `
  -WorkRoot .persistent-multi `
  -KeepRunning `
  -HostOverrideAssembly @($hostOverride)
```

Config is stored under `.persistent-multi/<ContainerName>/config` (relative to `lidarr.plugin.common/`).

### Convenience wrapper

Script: `scripts/test-multi-plugin-persistent.ps1`

```powershell
# Start (keeps config between runs, refreshes plugin files)
pwsh scripts/test-multi-plugin-persistent.ps1 -KeepRunning

# Force rebuild both plugin zips first
pwsh scripts/test-multi-plugin-persistent.ps1 -Rebuild -KeepRunning

# Wipe persisted state and start fresh
pwsh scripts/test-multi-plugin-persistent.ps1 -Clean -KeepRunning

# Run a real Lidarr AlbumSearch using existing UI-configured indexers
pwsh scripts/test-multi-plugin-persistent.ps1 -KeepRunning -RunSearchGate

# Run search + grab (requires indexers + download clients already configured in UI)
pwsh scripts/test-multi-plugin-persistent.ps1 -KeepRunning -RunGrabGate
```

## Running the Search gate against existing UI-configured indexers

If you configured indexers via the Lidarr UI (persisted config), you can run the search gate without providing env vars by enabling:
- `-UseExistingConfigForSearchGate`

Example:

```powershell
pwsh scripts/multi-plugin-docker-smoke-test.ps1 `
  -PluginZip @("qobuzarr=$q","tidalarr=$t") `
  -ContainerName lidarr-multi-plugin-persist `
  -Port 8691 `
  -PreserveState -WorkRoot .persistent-multi -KeepRunning `
  -RunSearchGate -UseExistingConfigForSearchGate
```

## Notes

- If you see `Bind for 0.0.0.0:<port> failed: port is already allocated`, change `-Port` and/or `-ContainerName`.
- Multi-plugin loading across independent plugin load contexts may still depend on upstream Lidarr fixes; `-HostOverrideAssembly` is intended for validating those before a new Docker tag exists.
