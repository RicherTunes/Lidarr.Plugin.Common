# E2E Troubleshooting Guide

> **Purpose**: Fast triage for E2E failures using structured error codes and diagnostics.

This guide helps you:
1. Identify the failure type from `errorCode` in `run-manifest.json`
2. Determine who owns the fix (Common library, plugin, or Lidarr host)
3. Take corrective action without downloading full artifacts

---

## Quick Triage Table: Runner Error Codes

These are `errorCode` values in `run-manifest.json` — distinct from `hostBugSuspected.classification` (see next section).

| `errorCode` | Meaning | First Fix | Owner |
|-------------|---------|-----------|-------|
| `E2E_AUTH_MISSING` | Required credentials missing | Set secrets/env vars; use `E2E_FORCE_CONFIG_UPDATE=1` | Plugin config |
| `E2E_CONFIG_INVALID` | Config exists but fails validation | Check field shapes; verify `redirectUrl`/market | Plugin config |
| `E2E_API_TIMEOUT` | Lidarr API call or polling timed out | Increase timeout; check Docker host load | Infra |
| `E2E_DOCKER_UNAVAILABLE` | Docker not available | Verify Docker daemon running | Infra |
| `E2E_NO_RELEASES_ATTRIBUTED` | Search returned releases but none from target plugin | Check `indexerId` attribution; verify indexer schema | Plugin |
| `E2E_QUEUE_NOT_FOUND` | Grab triggered but queue item missing | Inspect `queue-state.json`; verify DownloadProtocol | Plugin |
| `E2E_ZERO_AUDIO_FILES` | Download completed but no audio files | Check download client logs; verify output path | Plugin |
| `E2E_METADATA_MISSING` | Audio exists but required tags missing | Verify `IAudioMetadataApplier` wiring | Plugin/Common |
| `E2E_IMPORT_FAILED` | ImportListSync failed | Check `lastSyncError`; verify LLM endpoint | Plugin |
| `E2E_COMPONENT_AMBIGUOUS` | Multiple components match selection criteria | Use `-ComponentIdsInstanceSalt` or pin preferred IDs | E2E runner |
| `E2E_HOST_PLUGIN_DISCOVERY_DISABLED` | Plugins on disk but host not loading | Enable `use_host_override=always`; pin known-working tag | Host/E2E config |
| `E2E_ABSTRACTIONS_SHA_MISMATCH` | Plugins ship non-identical `Lidarr.Plugin.Abstractions.dll` bytes | Rebuild/bump submodules so Abstractions is byte-identical; rerun packaging preflight | Packaging/E2E |

<details>
<summary>Keeping this table in sync</summary>

To enumerate all `E2E_*` error codes in the codebase:

```bash
rg -o "E2E_[A-Z0-9_]+" scripts docs | cut -d: -f2 | sort -u
```

Compare output against this table to find undocumented codes.

</details>

---

## Host Failure Classifications

These are `hostBugSuspected.classification` values — a **separate axis** from runner error codes above. Check when `hostBugSuspected.detected == true`:

| Classification | Severity | Meaning | Fix |
|----------------|----------|---------|-----|
| `ALC` | `host_bug` | AssemblyLoadContext lifecycle issue | Report upstream with diagnostics bundle |
| `ABI_MISMATCH` | `plugin_rebuild` | Plugin built against incompatible assemblies | Rebuild against pinned host tag |
| `DEPENDENCY_DRIFT` | `version_conflict` | Assembly version conflicts across boundary | Verify packaging policy + dependency pins |
| `LOAD_FAILURE` | `investigate` | Generic load failure | Inspect `container-logs.txt`; validate package |
| `TYPE_INIT_FAILURE` | `investigate` | Type initializer failed | Check required settings and file paths |

---

## Decision Tree

```
E2E run failed
│
├─ Check run-manifest.json for `errorCode`
│   │
│   ├─ E2E_AUTH_MISSING?
│   │   └─ Set required secrets → Rerun with E2E_FORCE_CONFIG_UPDATE=1
│   │
│   ├─ E2E_CONFIG_INVALID?
│   │   └─ Verify field values in Lidarr UI → Check schema field names
│   │
│   ├─ E2E_API_TIMEOUT?
│   │   └─ Check Docker host resources → Increase timeout input
│   │
│   ├─ E2E_NO_RELEASES_ATTRIBUTED?
│   │   └─ Verify indexer credentials → Check indexerId in search results
│   │
│   ├─ E2E_QUEUE_NOT_FOUND?
│   │   └─ Inspect queue-state.json → Verify DownloadProtocol matches
│   │
│   ├─ E2E_ZERO_AUDIO_FILES?
│   │   └─ Check container-logs.txt → Verify download client output path
│   │
│   ├─ E2E_METADATA_MISSING?
│   │   └─ Verify IAudioMetadataApplier → Check StreamingTrack fields
│   │
│   ├─ E2E_COMPONENT_AMBIGUOUS?
│   │   └─ Add -ComponentIdsInstanceSalt → Or pin preferred IDs
│   │
│   ├─ E2E_HOST_PLUGIN_DISCOVERY_DISABLED?
│   │   └─ Enable use_host_override=always → Or pin to known-working tag
│   │
│   └─ E2E_ABSTRACTIONS_SHA_MISMATCH?
│       └─ Rebuild/bump submodules → Rerun packaging preflight
│
├─ No errorCode but failed?
│   │
│   └─ Check hostBugSuspected.detected
│       │
│       ├─ classification = ALC?
│       │   └─ Upstream Lidarr bug → Capture bundle + report
│       │
│       ├─ classification = ABI_MISMATCH?
│       │   └─ Rebuild plugins against pinned host tag
│       │
│       ├─ classification = DEPENDENCY_DRIFT?
│       │   └─ Run check-host-versions.ps1 -Strict → Fix pins
│       │
│       └─ classification = LOAD_FAILURE / TYPE_INIT_FAILURE?
│           └─ Inspect container-logs.txt → Check forbidden DLLs
│
└─ Still stuck?
    └─ Download diagnostics bundle → See "Attach This" section below
```

---

## Gate-Specific Troubleshooting

### Schema Gate Fails

**Symptoms**: Plugin not visible in Lidarr; schema endpoint returns empty/error.

**Check**:
1. `container-logs.txt` for `ReflectionTypeLoadException`
2. `hostBugSuspected.classification` in manifest
3. Packaging preflight (forbidden DLLs present?)

**Common causes**:
- Plugin built against wrong Lidarr tag
- `Lidarr.Plugin.Abstractions.dll` shipped in package (forbidden)
- TFM mismatch (net6.0 plugin in net8.0 host)

### Search/AlbumSearch Gate Fails

**Symptoms**: `E2E_NO_RELEASES_ATTRIBUTED` or zero results.

**Check**:
1. Indexer configured and enabled in Lidarr?
2. `indexerId` field populated in search results?
3. Search query returning results from other indexers?

**Common causes**:
- Parser regression (missing `indexerId` attribution)
- Indexer not configured/enabled
- Cached results from other indexers masking plugin results

### Grab Gate Fails

**Symptoms**: `E2E_QUEUE_NOT_FOUND` or `E2E_ZERO_AUDIO_FILES`.

**Check**:
1. `queue-state.json` for enqueued items
2. Download client logs for auth/API failures
3. Output directory permissions and path validity

**Common causes**:
- Download client auth expired
- DownloadProtocol mismatch between indexer and download client
- Output path doesn't exist or not writable

### Metadata Gate Fails

**Symptoms**: `E2E_METADATA_MISSING` - audio files exist but tags missing.

**Check**:
1. `IAudioMetadataApplier` registered in DI?
2. `StreamingTrack` model populated correctly?
3. TagLib warnings in logs?

**Common causes**:
- Metadata applier not wired in plugin DI
- Incomplete `StreamingTrack` mapping (missing artist/album/title)
- Container format not supported by TagLib

---

## Runner Toggles Reference

| Toggle | Purpose |
|--------|---------|
| `E2E_FORCE_CONFIG_UPDATE=1` | Overwrite masked `********` fields |
| `E2E_VALIDATE_METADATA=1` | Run opt-in metadata gate |
| `E2E_POST_RESTART_GRAB=1` | Additional grab validation after restart |
| `E2E_RUN_BRAINARR_LLM=1` | Enable Brainarr LLM gate |
| `E2E_STRICT_BRAINARR=1` | Treat Brainarr failures as FAIL (not SKIP) |
| `E2E_STRICT_PREREQS=1` | Convert credential SKIPs to FAILs |
| `E2E_DISABLE_FUZZY_COMPONENT_MATCH=1` | Disable fuzzy component selection |

---

## Attach This: Diagnostics Bundle

When filing an issue or requesting help, attach:

### Required

1. **`run-manifest.json`** - Contains:
   - `errorCode` - Primary failure classification
   - `hostBugSuspected` - Host failure classification
   - `lidarr.imageTag` / `lidarr.imageDigest` - Exact host version
   - `sources` - Plugin commit SHAs
   - Gate results with timestamps

2. **`diagnostics-<timestamp>.zip`** - Contains:
   - `container-logs.txt` - Lidarr container output
   - `queue-state.json` - Download queue snapshot
   - `search-results.json` - Search response (if applicable)
   - Plugin configuration (redacted)

### Redaction Rules

Before attaching, ensure these are redacted:

| Field | Action |
|-------|--------|
| API keys | Replace with `[REDACTED]` |
| Passwords | Replace with `[REDACTED]` |
| OAuth tokens | Replace with `[REDACTED]` |
| Session IDs | Replace with `[REDACTED]` |
| Email addresses | Replace with `user@example.com` |
| File paths with usernames | Replace username with `[USER]` |

### How to Download

```bash
# From GitHub Actions
gh run download <run-id> -n diagnostics

# From local run
# Bundle is in: .e2e-test-output/diagnostics-*.zip
```

---

## Related Documentation

- [E2E Error Codes](./E2E_ERROR_CODES.md) - Full error code reference
- [Host Failure Classification](./HOST_FAILURE_CLASSIFICATION.md) - `hostBugSuspected` details
- [Diagnostics Bundle Contract](./DIAGNOSTICS_BUNDLE_CONTRACT.md) - Bundle structure
- [Persistent E2E Testing](./PERSISTENT_E2E_TESTING.md) - Local E2E setup
- [E2E Preferred Component IDs](./dev-guide/E2E_PREFERRED_COMPONENT_IDS.md) - Disambiguation

---

## Keeping This Doc In Sync

To list all `E2E_*` codes in the codebase (for verifying this doc is complete):

```bash
# From repo root
rg -o "E2E_[A-Z0-9_]+" scripts docs | cut -d: -f2 | sort -u
```

If the grep output shows codes not in this doc, add them to the triage table.
