# Host Failure Classification

This document describes how the E2E runner classifies host/plugin load-time failures in `run-manifest.json` under `hostBugSuspected`.

The goal is fast, consistent triage:

- determine whether the failure is likely a Lidarr host bug (upstream),
- a plugin ABI mismatch (rebuild against the pinned host tag),
- a dependency/version conflict (packaging/pins),
- or a generic runtime/init issue (investigate logs).

## Where To Look

- `run-manifest.json` → `hostBugSuspected`
- `container-logs.txt` (diagnostics bundle) → plugin load / exception lines

## `hostBugSuspected` Structure (Manifest)

`hostBugSuspected` is always present for schema consistency:

- When not detected: `{ "detected": false }`
- When detected, fields may include:
  - `classification`: `ALC` | `ABI_MISMATCH` | `DEPENDENCY_DRIFT` | `LOAD_FAILURE` | `TYPE_INIT_FAILURE`
  - `severity`: `host_bug` | `plugin_rebuild` | `version_conflict` | `investigate`
  - `signature`: short name of the matched signature
  - `matchedLine`: a redacted log line excerpt used for classification
  - `description`: a human explanation

## Classifications

| Classification | Severity | Meaning | Typical Fix |
|---|---:|---|---|
| `ALC` | `host_bug` | AssemblyLoadContext lifecycle/unload issues (often upstream Lidarr). | Capture bundle + report upstream with host version/digest. |
| `ABI_MISMATCH` | `plugin_rebuild` | Plugin built against incompatible Lidarr assemblies (method/type mismatch). | Rebuild plugins against the pinned Lidarr Docker tag + extracted assemblies. |
| `DEPENDENCY_DRIFT` | `version_conflict` | Conflicting assembly versions across host/plugin boundaries. | Verify packaging policy + dependency pins (host-coupled deps). |
| `LOAD_FAILURE` | `investigate` | Generic load failure without a strong signature. | Inspect `container-logs.txt`, validate package contents and forbidden DLLs. |
| `TYPE_INIT_FAILURE` | `investigate` | Type initializer failure (often config/env-dependent). | Inspect logs; validate required settings and file permissions/paths. |

## Fast Triage Checklist

1. Is `hostBugSuspected.detected == true`?
   - If yes, start with its `classification` and `matchedLine`.
2. If `ABI_MISMATCH`:
   - confirm `lidarr.imageTag` / `lidarr.imageDigest` in manifest,
   - rebuild plugin packages against the same host binaries.
3. If `DEPENDENCY_DRIFT`:
   - verify packaging preflight results (forbidden DLLs),
   - confirm host-coupled dependency pins.
4. If `ALC`:
   - reproduce with the same host image digest,
   - file upstream issue with the diagnostics bundle and minimal reproduction.

