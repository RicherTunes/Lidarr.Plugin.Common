# E2E Error Codes

This document describes the structured `errorCode` values emitted into `run-manifest.json` by the E2E runner.

These codes are intended for:

- CI triage (job summaries, dashboards)
- consistent human debugging without downloading full artifacts

For diagnostics bundle structure, see `docs/DIAGNOSTICS_BUNDLE_CONTRACT.md`.

## Error Code Sources

Error codes can be determined in two ways:

1. **Explicit emission** (preferred): Plugins emit error codes directly via `PluginError.Metadata["e2eErrorCode"]` using the `E2EErrorCodeExtensions` helper methods.

2. **Pattern inference** (fallback): The E2E runner infers codes from error message text using regex patterns.

Explicit codes are always preferred when present. The manifest includes a `source` field indicating whether the code was `explicit` or `inferred`.

### Emitting Explicit Codes from Plugins

```csharp
using Lidarr.Plugin.Abstractions.Results;

// Option 1: Derive E2E code from PluginErrorCode
var error = E2EErrorCodeExtensions.CreateWithE2ECode(
    PluginErrorCode.Unauthorized,
    "Authentication failed");
// Metadata will contain: { "e2eErrorCode": "E2E_AUTH_MISSING" }

// Option 2: Specify explicit E2E code
var error = E2EErrorCodeExtensions.CreateWithE2ECode(
    PluginErrorCode.RateLimited,
    E2EErrorCode.RateLimited,
    "Too many requests");
// Metadata will contain: { "e2eErrorCode": "E2E_RATE_LIMITED" }

// Option 3: Add E2E code to existing error
var error = new PluginError(PluginErrorCode.Timeout, "Request timed out")
    .WithDerivedE2EErrorCode();
```

## Error Code Reference

| `errorCode` | Meaning | Common Causes | First Fix To Try |
|---|---|---|---|
| `E2E_AUTH_MISSING` | Required credentials are missing for the requested gate. | Secrets/env vars not set; Lidarr component not configured; masked `********` values but no overwrite requested. | Set required secrets/env vars; rerun Configure; use `E2E_FORCE_CONFIG_UPDATE=1` when rotating tokens. |
| `E2E_CONFIG_INVALID` | Configuration exists but fails validation (server-side reject). | Wrong `redirectUrl`; invalid market; missing download path; invalid field shape. | Check component fields in Lidarr UI/API; rerun with `E2E_FORCE_CONFIG_UPDATE=1`; ensure schema field names match. |
| `E2E_API_TIMEOUT` | A Lidarr API call or polling loop timed out. | Lidarr slow startup; Docker host under load; network hiccup; gate timeout too low. | Increase timeout input; re-run; inspect `container-logs.txt` and command status endpoints. |
| `E2E_DOCKER_UNAVAILABLE` | Docker interaction required but not available. | Docker not installed/running; insufficient permissions; wrong container name. | Verify Docker daemon; set correct `-ContainerName`; run schema gate without docker-only features. |
| `E2E_NO_RELEASES_ATTRIBUTED` | AlbumSearch returned releases but none attributed to the target plugin. | Indexer not actually used; parser regression (missing indexerId/indexer); wrong indexer id; cached results from other indexers. | Re-run AlbumSearch; check `indexerId` attribution warnings; verify plugin schema + configured indexer id. |
| `E2E_QUEUE_NOT_FOUND` | Grab triggered but expected queue item could not be correlated. | Download client didn't enqueue; correlation logic mismatch; Lidarr API change; auth failure masked as enqueue. | Inspect `queue-state.json`; check download client logs; verify DownloadProtocol matches release protocol. |
| `E2E_ZERO_AUDIO_FILES` | Download completed but produced zero validated audio files. | Download client bug; output path wrong; partial files only; upstream stream failure; file extension filtering mismatch. | Inspect output directory; check `container-logs.txt` for download errors; verify validator settings and file naming. |
| `E2E_METADATA_MISSING` | Audio file(s) exist but required tags are missing. | Metadata applier not wired; TagLib limitations for container format; incomplete track model mapping. | Verify `IAudioMetadataApplier` wiring; validate `StreamingTrack` fields; inspect TagLib warnings. |
| `E2E_IMPORT_FAILED` | ImportListSync completed with errors or post-sync state indicates failure. | Provider unreachable; LLM endpoint down; Brainarr import list misconfigured; auth expired. | Check `lastSyncError`; verify LLM endpoint reachability; rerun with longer timeout. |
| `E2E_RATE_LIMITED` | Upstream provider rate limited the request. | Too many requests; quota exceeded; temporary throttling. | Wait and retry; check rate limit headers; reduce request frequency. |
| `E2E_PROVIDER_UNAVAILABLE` | Upstream provider service is unavailable. | Outage; maintenance window; network partition. | Check provider status page; retry later; verify network connectivity. |
| `E2E_CANCELLED` | Operation was cancelled. | User cancellation; timeout cancellation; graceful shutdown. | Check cancellation source; increase timeout if needed. |
| `E2E_COMPONENT_AMBIGUOUS` | Multiple configured components match the plugin. | Duplicate indexers/download clients with same implementation name. | Delete duplicate components; use preferred ID env var. |
| `E2E_LOAD_FAILURE` | Assembly or type loading failure. | ALC bug; ABI mismatch; dependency drift; missing dependencies. | Rebuild plugin; check host version compatibility; see `hostBugSuspected`. |

## Related Runner Toggles

These are not `errorCode` values, but common toggles referenced in logs/docs:

- `E2E_FORCE_CONFIG_UPDATE=1` — overwrite masked `********` fields during Configure.
- `E2E_VALIDATE_METADATA=1` — run opt-in metadata gate after successful Grab.
- `E2E_POST_RESTART_GRAB=1` — perform an additional post-restart Grab validation.
- `E2E_RUN_BRAINARR_LLM=1` — enable Brainarr LLM gate when `BRAINARR_LLM_BASE_URL` is set.
- `E2E_STRICT_BRAINARR=1` — treat Brainarr LLM failures as FAIL instead of SKIP.

## Host/Load Failures

If a run fails very early (e.g. schema cannot load plugins), consult:

- `hostBugSuspected` in `run-manifest.json` (classification + severity)
- `docs/HOST_FAILURE_CLASSIFICATION.md`
