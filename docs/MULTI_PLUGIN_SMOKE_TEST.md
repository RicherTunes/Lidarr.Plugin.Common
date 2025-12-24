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

### Always Required

| Secret | Description |
|--------|-------------|
| `CROSS_REPO_PAT` | GitHub Personal Access Token with `repo` scope for Qobuzarr and Tidalarr checkout |

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

Secrets can use either `QOBUZARR_*` or `QOBUZ_*` prefix (and similarly `TIDALARR_*` or `TIDAL_*`).

## Workflow Inputs

| Input | Type | Default | Description |
|-------|------|---------|-------------|
| `lidarr_tag` | string | `pr-plugins-2.14.2.4786` | Lidarr Docker image tag (plugins branch) |
| `qobuzarr_ref` | string | `main` | Qobuzarr branch/tag/SHA to test |
| `tidalarr_ref` | string | `main` | Tidalarr branch/tag/SHA to test |
| `run_medium_gate` | boolean | `false` | Configure and test indexers (requires credentials) |
| `run_downloadclient_gate` | boolean | `false` | Configure and test download clients (requires credentials) |
| `run_search_gate` | boolean | `false` | Run AlbumSearch and verify releases (requires credentials) |
| `require_all_indexers_in_search` | boolean | `false` | Fail if releases don't include all configured indexers |
| `run_canary` | boolean | `false` | Also run against moving `pr-plugins` tag (allowed to fail) |

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

### Canary (`run_canary=true`)
- Runs all enabled gates against the moving `pr-plugins` Docker tag
- Allowed to fail (detects breaking changes early)
- Matrix entry with `continue-on-error: true`

## Copy-Paste Examples

### Minimal "Known Good" Run (Basic Gate)

Only requires `CROSS_REPO_PAT`:

```bash
# Basic gate - verify plugins load in Lidarr
gh workflow run multi-plugin-smoke-test.yml \
  --repo RicherTunes/Lidarr.Plugin.Common \
  --ref main
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

## Activation Checklist

1. **Add CROSS_REPO_PAT secret**
   - Go to: https://github.com/settings/tokens
   - Create a PAT (classic) with `repo` scope
   - Add to: https://github.com/RicherTunes/Lidarr.Plugin.Common/settings/secrets/actions

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
The workflow requires a PAT to checkout private plugin repositories. See "Required Secrets" above.

### Plugin not appearing in schema
- Check container logs for assembly load errors
- Verify plugin.json manifest is correct
- Check for version mismatches with Lidarr.Core

### Credential validation failed
- Verify secret names match expected prefixes (`QOBUZ_*` or `QOBUZARR_*`)
- Check that email/password OR user_id/auth_token are provided (not partial)

## See Also

- [CI & automation](dev-guide/CI.md)
- [Unified plugin pipeline](dev-guide/UNIFIED_PLUGIN_PIPELINE.md)
