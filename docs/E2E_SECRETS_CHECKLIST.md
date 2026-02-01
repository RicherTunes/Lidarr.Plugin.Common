# E2E Secrets Checklist

This document lists all secrets required for E2E testing and their purpose.

## Quick Reference

| Gate | Required Secrets |
|------|------------------|
| Basic | `CROSS_REPO_PAT` only |
| AuthFail-Redaction | `CROSS_REPO_PAT` only |
| Medium/Search/Grab | Provider credentials (see below) |
| Drift Sentinel (error mode) | `CROSS_REPO_PAT` only |
| Drift Sentinel (success mode) | Provider credentials for API calls |

## Secret Categories

### Required for All E2E

| Secret | Description | Where to Get |
|--------|-------------|--------------|
| `CROSS_REPO_PAT` | GitHub PAT with `repo` scope | [GitHub Settings > Tokens](https://github.com/settings/tokens) |

### Qobuz Credentials

For Medium/Search/Grab gates and Drift Sentinel success mode:

| Secret | Description | Required For |
|--------|-------------|--------------|
| `QOBUZ_EMAIL` | Qobuz account email | Medium gate (login) |
| `QOBUZ_PASSWORD` | Qobuz account password | Medium gate (login) |
| `QOBUZ_APP_ID` | Qobuz application ID | All API calls |
| `QOBUZ_APP_SECRET` | Qobuz application secret | Authenticated calls |
| `QOBUZ_AUTH_TOKEN` | Pre-authenticated token | Drift sentinel success mode |
| `QOBUZ_COUNTRY_CODE` | Market (e.g., `US`) | Search/quality filtering |

**Alternative naming**: `QOBUZARR_*` prefix also supported.

### Tidal Credentials

For Medium/Search/Grab gates:

| Secret | Description | Required For |
|--------|-------------|--------------|
| `TIDAL_REDIRECT_URL` | OAuth redirect URL | Medium gate (OAuth flow) |
| `TIDAL_MARKET` | Market code (e.g., `US`) | Search/quality filtering |

For Drift Sentinel success mode (OAuth client credentials flow):

| Secret | Description | Required For |
|--------|-------------|--------------|
| `TIDAL_CLIENT_ID` | Tidal API client ID | OAuth token acquisition |
| `TIDAL_CLIENT_SECRET` | Tidal API client secret | OAuth token acquisition |

**Alternative naming**: `TIDALARR_*` prefix also supported.

## Mode-Specific Requirements

### PR E2E (Hermetic Mode)

Only requires:
- `CROSS_REPO_PAT`

**Security note**: PR workflows NEVER use success-mode credentials. This is enforced in `e2e-pr-hermetic.yml`:
```yaml
drift_sentinel_include_success_mode: false
```

### Nightly E2E (Live Mode)

Requires full credentials for enabled gates:
- `CROSS_REPO_PAT`
- Qobuz: `QOBUZ_APP_ID` + `QOBUZ_AUTH_TOKEN` (for success mode)
- Tidal: `TIDAL_CLIENT_ID` + `TIDAL_CLIENT_SECRET` (for OAuth flow)

### Drift Sentinel Strict Mode

When `drift_sentinel_fail_on_drift: true` AND `drift_sentinel_include_success_mode: true`:
- **Workflow will fail** if no credentials are configured
- Preflight check validates secrets before running

## Workflow Preflight Checks

The smoke test workflow includes validation steps:

1. **CROSS_REPO_PAT health check**: Validates token is not expired
2. **Live gate credentials check**: Validates provider creds for medium/search/grab gates
3. **Drift sentinel strict mode check**: Validates API credentials when strict success mode enabled

If validation fails, the workflow provides clear instructions on which secrets are missing.

## Adding Secrets to Repository

1. Go to: `Repository Settings > Secrets and variables > Actions`
2. Click "New repository secret"
3. Add each required secret

For organization-level secrets:
1. Go to: `Organization Settings > Secrets and variables > Actions`
2. Add secrets and grant repository access

## Security Best Practices

1. **Never log secrets**: All workflows mask secret values
2. **PR isolation**: PR workflows cannot access success-mode credentials
3. **Minimum scope**: Only grant necessary permissions to PAT
4. **Rotate regularly**: Especially after personnel changes
5. **Audit usage**: Review Actions logs for unexpected secret access

## Troubleshooting

### "CROSS_REPO_PAT secret is missing or empty"

The workflow skips when this secret is not configured. Solution:
1. Create PAT at https://github.com/settings/tokens
2. Grant `repo` scope (classic) or fine-grained read access
3. Add to repository secrets as `CROSS_REPO_PAT`

### "Drift sentinel strict success mode requires credentials"

When strict mode is enabled without credentials. Solutions:
1. Add the required provider secrets (see above)
2. Or disable strict mode: `drift_sentinel_fail_on_drift: false`
3. Or disable success mode: `drift_sentinel_include_success_mode: false`

### "Tidal OAuth failed: Invalid client credentials"

The `TIDAL_CLIENT_ID` or `TIDAL_CLIENT_SECRET` is incorrect. Solutions:
1. Verify credentials in Tidal developer portal
2. Ensure no whitespace/newlines in secret values
3. Check if application is still active

## See Also

- [Multi-Plugin Smoke Test](MULTI_PLUGIN_SMOKE_TEST.md) - Full E2E documentation
- [CI Lane Strategy](CI_LANE_STRATEGY.md) - PR vs nightly gate strategy
