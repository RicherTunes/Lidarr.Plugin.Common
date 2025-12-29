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

## E2E Runner (`e2e-runner.ps1`)

The unified E2E runner provides gate-based testing with automatic credential detection.

### Basic Usage

```powershell
# Schema gate only (no credentials needed)
pwsh scripts/e2e-runner.ps1 `
  -Plugins 'Qobuzarr,Tidalarr,Brainarr' `
  -Gate schema `
  -LidarrUrl 'http://localhost:8691' `
  -ContainerName 'lidarr-multi-plugin-persist' `
  -ExtractApiKeyFromContainer

# All gates (will SKIP credential-gated tests if creds missing)
pwsh scripts/e2e-runner.ps1 `
  -Plugins 'Qobuzarr' `
  -Gate all `
  -LidarrUrl 'http://localhost:8691' `
  -ContainerName 'lidarr-multi-plugin-persist' `
  -ExtractApiKeyFromContainer
```

### Configure + Persistence

To reduce “split-brain” config errors (e.g., OAuth configured on the indexer but
missing on the download client), the runner includes two optional gates:

| Gate | Purpose |
|------|---------|
| `configure` | Fixes known config drift that causes E2E failures (currently: sync Tidalarr `redirectUrl` + `configPath` from indexer → download client when missing). |
| `persist` | Restarts the Lidarr Docker container and verifies configured components still exist after restart. |
| `bootstrap` | Runs `configure` + `all` + `persist` in one command. |

By default, `bootstrap` also performs a **post-restart Search revalidation** to prove auth still works after the container restart.
You can enable the same post-restart revalidation for `persist` by passing `-PersistRerun`.

```powershell
# Sync known configuration drift (no-op if already consistent)
pwsh scripts/e2e-runner.ps1 -Plugins 'Tidalarr' -Gate configure `
  -LidarrUrl 'http://localhost:8691' -ContainerName 'lidarr-multi-plugin-persist' -ExtractApiKeyFromContainer

# Restart + verify config objects still exist
pwsh scripts/e2e-runner.ps1 -Plugins 'Qobuzarr,Tidalarr,Brainarr' -Gate persist `
  -LidarrUrl 'http://localhost:8691' -ContainerName 'lidarr-multi-plugin-persist' -ExtractApiKeyFromContainer

# Restart + prove auth still works post-restart (re-runs Search gate after restart)
pwsh scripts/e2e-runner.ps1 -Plugins 'Qobuzarr,Tidalarr' -Gate persist -PersistRerun `
  -LidarrUrl 'http://localhost:8691' -ContainerName 'lidarr-multi-plugin-persist' -ExtractApiKeyFromContainer

# Full: fix drift → run all gates → restart → verify persistence
pwsh scripts/e2e-runner.ps1 -Plugins 'Qobuzarr,Tidalarr,Brainarr' -Gate bootstrap `
  -LidarrUrl 'http://localhost:8691' -ContainerName 'lidarr-multi-plugin-persist' -ExtractApiKeyFromContainer
```

### Gate Cascade and SKIP Behavior

When running `-Gate all`, gates execute in order: **Schema → Search → AlbumSearch → Grab**

| Gate | Credential Required | SKIP Behavior |
|------|---------------------|---------------|
| Schema | No | Never skips |
| Search | Yes (indexer creds) | SKIPs if credentials not configured |
| AlbumSearch | Yes (indexer creds) | SKIPs if credentials not configured |
| Grab | Yes (indexer + download client creds) | SKIPs if credentials not configured |

**SKIP vs FAIL semantics:**
- **SKIP (yellow)**: Credentials not configured or auth error detected (e.g., `invalid_grant`, `unauthorized`)
- **FAIL (red)**: Real regression - API error, attribution bug, file validation failure

### Credential Detection

The runner checks indexer/download client field values:

```powershell
# All-of: ALL listed fields must have values
CredentialFieldNames = @("configPath")

# Any-of: AT LEAST ONE field must have a value
CredentialAnyOfFieldNames = @("authToken", "password")  # Qobuzarr: token OR password auth
CredentialAnyOfFieldNames = @("redirectUrl", "oauthRedirectUrl")  # Tidalarr: OAuth configured
```

### Diagnostics and Redaction

The runner automatically redacts sensitive fields in output:
- `password`, `secret`, `token`, `apikey`, `auth` patterns
- OAuth tokens and refresh tokens
- PKCE verifiers and authorization codes

To collect diagnostics without exposing secrets:
```powershell
pwsh scripts/e2e-runner.ps1 -Plugins 'Qobuzarr' -Gate all ... 2>&1 | Tee-Object -FilePath e2e-output.log
# Output is pre-redacted; safe to share
```

## Notes

- If you see `Bind for 0.0.0.0:<port> failed: port is already allocated`, change `-Port` and/or `-ContainerName`.
- Multi-plugin loading across independent plugin load contexts may still depend on upstream Lidarr fixes; `-HostOverrideAssembly` is intended for validating those before a new Docker tag exists.
