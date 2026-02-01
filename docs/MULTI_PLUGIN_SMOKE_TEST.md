# Multi-Plugin Smoke Test

The multi-plugin smoke test workflow verifies that multiple plugins (Qobuzarr and Tidalarr) can load and function correctly together in a Lidarr instance.

## Quick Start (Basic Gate Only)

Run the workflow with all gates disabled to verify basic schema loading:

```bash
gh workflow run multi-plugin-smoke-test.yml \
  --ref main \
  -f run_medium_gate=false \
  -f run_downloadclient_gate=false \
  -f run_search_gate=false \
  -f run_canary=false
```

## Required Secrets

### Required To Run (Otherwise Skips)

| Secret | Description |
|--------|-------------|
| `CROSS_REPO_PAT` | GitHub Personal Access Token with `repo` scope for cross-repo checkout (Qobuzarr/Tidalarr/Brainarr) |

If `CROSS_REPO_PAT` is not configured, the workflow prints a notice and skips the smoke test instead of failing.

### For Medium/Search Gates

| Secret | Description | When Required |
|--------|-------------|---------------|
| `QOBUZ_EMAIL` | Qobuz account email | Medium/Search gates |
| `QOBUZ_PASSWORD` | Qobuz account password | Medium/Search gates |
| `QOBUZ_USER_ID` | Qobuz user ID (alternative to email/password) | Medium/Search gates |
| `QOBUZ_AUTH_TOKEN` | Qobuz auth token (alternative to email/password) | Medium/Search gates |
| `QOBUZ_APP_ID` | Qobuz application ID | Medium/Search gates |
| `QOBUZ_APP_SECRET` | Qobuz application secret | Medium/Search gates |
| `TIDAL_REDIRECT_URL` | Tidal OAuth redirect URL | Medium/Search gates |
| `TIDAL_MARKET` | Tidal market code (e.g., `US`) | Medium/Search gates |
| `TIDAL_CLIENT_ID` | Tidal API client ID | Drift sentinel success mode |
| `TIDAL_CLIENT_SECRET` | Tidal API client secret | Drift sentinel success mode |

Secrets can use either `QOBUZARR_*` or `QOBUZ_*` prefix (and similarly `TIDALARR_*` or `TIDAL_*`).

## Workflow Inputs

| Input | Type | Default | Description |
|-------|------|---------|-------------|
| `lidarr_tag` | string | `pr-plugins-3.1.1.4884` | Lidarr Docker image tag (plugins branch; must support net8 plugins) |
| `qobuzarr_ref` | string | `main` | Qobuzarr branch/tag/SHA to test |
| `tidalarr_ref` | string | `main` | Tidalarr branch/tag/SHA to test |
| `run_medium_gate` | boolean | `false` | Configure and test indexers (requires credentials) |
| `run_downloadclient_gate` | boolean | `false` | Configure and test download clients (requires credentials) |
| `run_search_gate` | boolean | `false` | Run AlbumSearch and verify releases (requires credentials) |
| `run_grab_gate` | boolean | `false` | POST `/api/v1/release` to queue a download (requires credentials) |
| `require_downloaded_files` | boolean | `false` | Grab gate also requires files on disk (slow; local use) |
| `grab_timeout_seconds` | string | `300` | Grab gate timeout for queue/files checks |
| `require_all_indexers_in_search` | boolean | `false` | Fail if releases don't include all configured indexers |
| `run_canary` | boolean | `false` | Also run against moving `pr-plugins` tag (allowed to fail) |
| `run_golden_persist_gate` | boolean | `false` | Restart container and verify state persistence (requires grab gate) |
| `run_authfail_redaction_gate` | boolean | `false` | Test auth failure handling and log redaction (no creds needed) |
| `run_drift_sentinel_gate` | boolean | `false` | Validate stub-vs-live API field expectations (nightly only) |
| `drift_sentinel_fail_on_drift` | boolean | `false` | Fail workflow if drift detected (warning mode if false) |
| `drift_sentinel_include_success_mode` | boolean | `false` | Also validate authenticated success payloads (requires credentials) |
| `e2e_mode` | string | `live` | E2E mode: `hermetic` (stubbed, PR-safe) or `live` (real APIs, nightly) |

## Gates

### Basic Gate (Always Runs)
- Builds both plugins
- Starts Lidarr with plugins mounted
- Verifies plugins appear in `/api/v1/indexer/schema` and `/api/v1/downloadclient/schema`

### Medium Gate (`run_medium_gate=true`)
- Configures indexers via POST to `/api/v1/indexer`
- Tests indexer connectivity via POST to `/api/v1/indexer/test`
- Requires provider credentials

### Download Client Gate (`run_downloadclient_gate=true`)
- Configures download clients via POST to `/api/v1/downloadclient`
- Tests download client connectivity via POST to `/api/v1/downloadclient/test`
- Requires provider credentials

### Search Gate (`run_search_gate=true`)
- Seeds a test artist
- Runs AlbumSearch command
- Verifies releases are returned
- Optionally requires all indexers in results (`require_all_indexers_in_search`)

### Grab Gate (`run_grab_gate=true`)
- Queues a release download via POST to `/api/v1/release`
- Verifies the grabbed release appears in `/api/v1/queue`
- Optionally requires files to exist on disk (`require_downloaded_files=true`)
- If only one provider is configured, the gate runs for that provider and skips the others

### Golden-Persist Gate (`run_golden_persist_gate=true`)
- Runs after grab gate completes (requires grab + downloaded files)
- Restarts the Lidarr container
- Verifies after restart:
  - Plugin still loads (schemas available)
  - Queue/history state persists (no duplicate grabs)
  - Telemetry signal still emitted (if telemetry gate was also enabled)
- Catches the most common real-world regression: data loss on restart

### AuthFail-Redaction Gate (`run_authfail_redaction_gate=true`)
- Tests auth failure handling and secret redaction
- Configures plugin with intentionally bad credentials
- Verifies:
  - Operation fails gracefully (HTTP 401/403/429, no crash)
  - Error responses do not leak secrets
  - Container logs do not leak secrets (query strings, bearer tokens redacted)
- **Does NOT require real credentials** - safe for PR CI

### Canary (`run_canary=true`)
- Runs all enabled gates against the moving `pr-plugins` Docker tag
- Allowed to fail (detects breaking changes early)
- Matrix entry with `continue-on-error: true`

### Drift Sentinel Gate (`run_drift_sentinel_gate=true`)
- Validates stub-vs-live API field expectations
- Makes minimal live probes per provider (auth + search endpoints)
- Compares response structure against what stubs depend on
- **Error mode** (default): Probes auth endpoints with invalid creds - no secrets needed
- **Success mode** (`drift_sentinel_include_success_mode=true`): Also validates authenticated search payloads - requires credentials, auto-skips if missing
- **Warning mode** (`drift_sentinel_fail_on_drift=false`): logs drift but doesn't fail
- **Strict mode** (`drift_sentinel_fail_on_drift=true`): fails workflow on drift
- Rate limiting: Uses exponential backoff, respects Retry-After headers, treats 429 as inconclusive (not failure)
- Field validation: "Required" fields must be present; "AtLeastOne" fields need at least one item to have the field
- Versioned expectations: Tracks expectations version for drift triage
- Catches API breaking changes before they break hermetic E2E
- Runs in nightly E2E (not PR E2E - needs live API access)
- **Auto-issue**: Creates/updates a GitHub issue when drift is detected (nightly only)

#### Tidal OAuth Token Acquisition

For Tidal success-mode probes, the drift sentinel can automatically acquire OAuth tokens:

1. **Client Credentials Flow** (preferred): Set `TIDAL_CLIENT_ID` and `TIDAL_CLIENT_SECRET`
   - Token is acquired automatically before success-mode probes
   - Short-lived tokens, no manual refresh needed

2. **Manual Token** (fallback): Set `TIDAL_ACCESS_TOKEN` directly
   - Requires manual token refresh when expired
   - Use for testing when client credentials unavailable

#### Strictness Promotion Policy

Promotion to strict mode is based on **consecutive clean runs**, not calendar time:

| Provider | Threshold | Criteria |
|----------|-----------|----------|
| Qobuz | 5 consecutive nights | No drift, no errors, ≤10% inconclusive |
| Tidal | 7 consecutive nights | No drift, no errors, ≤10% inconclusive |

**Blockers for promotion:**
- Open reliability issue (consecutive 429s)
- Any drift in the pass streak window
- Inconclusive rate > 10%

**Check promotion readiness:**
```bash
./scripts/drift-strictness-check.ps1
```

**To promote:**
1. Verify `drift-strictness-check.ps1` shows READY
2. Edit `e2e-nightly-live.yml`:
   ```yaml
   drift_sentinel_fail_on_drift: true
   ```
3. Monitor first strict run for unexpected failures

## E2E Modes

### Live Mode (Default)
- Uses real service APIs (Qobuz, Tidal, etc.)
- Requires valid credentials in secrets
- Recommended for: nightly runs, manual validation

### Hermetic Mode (`e2e_mode=hermetic`)
- Uses stub HTTP server for API responses
- **No credentials required** - safe for PR CI
- Verifies plugin behavior without external dependencies
- Limited to: basic gate, authfail-redaction gate
- Recommended for: PR validation, cost-sensitive CI

## Copy-Paste Examples

### Minimal "Known Good" Run (Basic Gate)

Only requires `CROSS_REPO_PAT`:

```bash
# Basic gate - verify plugins load in Lidarr
gh workflow run multi-plugin-smoke-test.yml \
  --repo RicherTunes/Lidarr.Plugin.Common \
  --ref main
```

### AuthFail-Redaction Gate (PR-Safe)

Only requires `CROSS_REPO_PAT` - no service credentials needed:

```bash
# AuthFail gate - test failure handling and log redaction
gh workflow run multi-plugin-smoke-test.yml \
  --repo RicherTunes/Lidarr.Plugin.Common \
  --ref main \
  -f run_authfail_redaction_gate=true
```

### Full Search Gate Run

Requires these secrets to be configured first:

| Secret | Example Value |
|--------|---------------|
| `CROSS_REPO_PAT` | `ghp_xxxx...` (PAT with `repo` scope) |
| `QOBUZ_EMAIL` | `user@example.com` |
| `QOBUZ_PASSWORD` | `your-password` |
| `QOBUZ_APP_ID` | `123456789` |
| `QOBUZ_APP_SECRET` | `abcdef...` |

```bash
# Search gate - full end-to-end with album search
gh workflow run multi-plugin-smoke-test.yml \
  --repo RicherTunes/Lidarr.Plugin.Common \
  --ref main \
  -f run_medium_gate=true \
  -f run_search_gate=true \
  -f run_canary=true
```

### Golden-Persist Gate (Full Durability Test)

Requires all credentials plus downloaded files:

```bash
# Golden-persist gate - restart + verify persistence
gh workflow run multi-plugin-smoke-test.yml \
  --repo RicherTunes/Lidarr.Plugin.Common \
  --ref main \
  -f run_medium_gate=true \
  -f run_search_gate=true \
  -f run_grab_gate=true \
  -f require_downloaded_files=true \
  -f run_golden_persist_gate=true \
  -f grab_timeout_seconds=600
```

### Nightly Full E2E (All Gates)

```bash
# Full nightly - all gates including persistence and security
gh workflow run multi-plugin-smoke-test.yml \
  --repo RicherTunes/Lidarr.Plugin.Common \
  --ref main \
  -f run_medium_gate=true \
  -f run_downloadclient_gate=true \
  -f run_search_gate=true \
  -f run_grab_gate=true \
  -f require_downloaded_files=true \
  -f run_golden_persist_gate=true \
  -f run_authfail_redaction_gate=true \
  -f run_canary=true \
  -f grab_timeout_seconds=600
```

## Activation Checklist

1. **Add CROSS_REPO_PAT secret**
   - Go to: https://github.com/settings/tokens
   - Create a PAT (classic) with `repo` scope
   - If running this workflow directly in `Lidarr.Plugin.Common`, add it to:
     - https://github.com/RicherTunes/Lidarr.Plugin.Common/settings/secrets/actions
   - If calling this workflow from a plugin repo via `workflow_call` + `secrets: inherit`, add it to the CALLER repo instead.

2. **Run Basic Gate**
   ```bash
   gh workflow run multi-plugin-smoke-test.yml --repo RicherTunes/Lidarr.Plugin.Common --ref main
   ```

3. **Enable Canary** (once Basic passes)
   ```bash
   gh workflow run multi-plugin-smoke-test.yml --repo RicherTunes/Lidarr.Plugin.Common --ref main -f run_canary=true
   ```

4. **Add Provider Credentials** (for Medium/Search gates)
   - Add Qobuz and/or Tidal secrets (see table above)
   - Enable `run_medium_gate=true`

## Artifacts

On failure (or always), the workflow uploads:
- Built plugin zips (`Qobuzarr*.zip`, `Tidalarr*.zip`)
- Staging directory (`.docker-multi-smoke-test/`)
- Container diagnostics:
  - `diagnostics/containers.txt` - All Docker containers (`docker ps -a`)
  - `diagnostics/container.log` - Lidarr container logs
  - `diagnostics/inspect.json` - Docker container inspect output

> **Security Note**: Container logs may contain sensitive data if a plugin logs credentials or tokens. Be cautious when enabling gates with real provider credentials, and review artifacts before sharing them publicly.

## Troubleshooting

### "CROSS_REPO_PAT secret is missing or empty"
The workflow will skip the smoke test (with a notice) when this secret is missing. Configure `CROSS_REPO_PAT` to enable cross-repo checkouts. See "Required Secrets" above.

### Local validation against an upstream Lidarr fix (host override)
When the Lidarr host has a known bug (e.g., plugin loader issues) and you want to validate a fix before a new `pr-plugins-*` image is published, you can run the harness locally and **override a host assembly** via a Docker bind mount.

Example (override `Lidarr.Common.dll`):

```powershell
# Build the patched host assembly from your local Lidarr checkout (disable analyzers if needed)
dotnet build D:\Alex\github\_upstream\Lidarr\src\NzbDrone.Common\Lidarr.Common.csproj -c Release `
  -p:RunAnalyzersDuringBuild=false -p:EnableNETAnalyzers=false -p:TreatWarningsAsErrors=false

# Run schema gate with host override
pwsh D:\Alex\github\lidarr.plugin.common\scripts\multi-plugin-docker-smoke-test.ps1 `
  -LidarrTag pr-plugins-3.1.1.4884 `
  -PluginZip @(
    "qobuzarr=D:\Alex\github\qobuzarr\artifacts\packages\qobuzarr-0.1.0-dev-net8.0.zip",
    "tidalarr=D:\Alex\github\tidalarr\src\Tidalarr\artifacts\packages\tidalarr-1.0.1-net8.0.zip"
  ) `
  -HostOverrideAssembly D:\Alex\github\_upstream\Lidarr\_output\net8.0\Lidarr.Common.dll
```

This is intended for local debugging only; the workflow continues to use published host images.

### Plugin not appearing in schema
- Check container logs for assembly load errors
- Verify plugin.json manifest is correct
- Check for version mismatches with Lidarr.Core

### Credential validation failed
- Verify secret names match expected prefixes (`QOBUZ_*` or `QOBUZARR_*`)
- Check that email/password OR user_id/auth_token are provided (not partial)

## Plugin Workflow Integration

Each plugin repository has two E2E workflows that call Common's reusable workflows:

### PR E2E (Hermetic Mode)

**File**: `.github/workflows/multi-plugin-smoke-test.yml`

```yaml
jobs:
  hermetic-e2e:
    uses: RicherTunes/Lidarr.Plugin.Common/.github/workflows/e2e-pr-hermetic.yml@main
    with:
      test_plugins: 'qobuzarr,tidalarr'
      caller_plugin: 'your-plugin'
    secrets:
      CROSS_REPO_PAT: ${{ secrets.CROSS_REPO_PAT }}
```

**Triggers**: Push to main, PRs, manual dispatch
**Gates**: Basic gate + AuthFail-Redaction
**Credentials**: Only `CROSS_REPO_PAT` required

### Nightly E2E (Live Mode)

**File**: `.github/workflows/e2e-nightly.yml`

```yaml
jobs:
  live-e2e:
    uses: RicherTunes/Lidarr.Plugin.Common/.github/workflows/e2e-nightly-live.yml@main
    with:
      test_plugins: 'qobuzarr,tidalarr'
    secrets:
      CROSS_REPO_PAT: ${{ secrets.CROSS_REPO_PAT }}
      # Plugin-specific credentials...
```

**Triggers**: Scheduled (03:00-04:00 UTC daily), manual dispatch
**Gates**: All gates including Golden-Persist and AuthFail-Redaction
**Credentials**: Full credentials for each plugin

### Schedule Staggering

To avoid rate limit conflicts and Docker cache collisions:
- **Common's canonical nightly E2E**: 02:00 UTC (runs first)
- **Qobuzarr nightly E2E**: 03:00 UTC
- **Tidalarr nightly E2E**: 04:00 UTC
- **Unit test nightlies**: 06:00 UTC

### Concurrency Controls

All E2E workflows have concurrency groups to prevent pile-up:
- **PR E2E**: `cancel-in-progress: true` - newer pushes cancel older runs
- **Nightly E2E**: `cancel-in-progress: false` - runs complete without interruption
- **Timeouts**: PR E2E = 20min, Nightly E2E = 45min

## See Also

- [CI & automation](dev-guide/CI.md)
- [Unified plugin pipeline](dev-guide/UNIFIED_PLUGIN_PIPELINE.md)
