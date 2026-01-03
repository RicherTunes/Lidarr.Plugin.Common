# E2E Stabilization Roadmap (Multi-Plugin)

## Current Goal

Make the GitHub Actions workflow `E2E Bootstrap (Headless)` deterministic and green for:

- `Qobuzarr` + `Tidalarr` streaming E2E: Schema → Search → AlbumSearch → Grab (+ optional gates)
- `Brainarr` import list E2E: Schema → ImportList (+ optional LLM proof gate)
- **Multi-plugin coexistence** in a single Lidarr container

## Known Host Behaviors (Important)

> **Note:** These observations are point-in-time snapshots. The **canary job** (`override=never` against
> `pr-plugins`) is the authoritative source for current host behavior.

1. **File-based plugin discovery works on** `ghcr.io/hotio/lidarr:pr-plugins-3.1.1.4884`
   - Plugins placed at `/config/plugins/<Vendor>/<PluginName>/` are loaded.
   - *Observed: 2025-01-02, Run #20669121388*
2. **File-based plugin discovery does not work on** `ghcr.io/hotio/lidarr:pr-plugins` (moving tag, observed `3.1.1.4901`)
   - `/api/v1/system/plugins` returns `[]` and indexer/download client schemas do not include plugins.
   - Treat this as a host behavior change; do not assume folder scanning.
   - *Observed: 2025-01-01, Run #20654789xxx (approximate)*

**When these observations become stale:** Check the latest canary job results or run `override=never` manually.

## Current Blocker (P0) — RESOLVED

**Multi-plugin load failure on `3.1.1.4884`** (now mitigated via host override):

- Logs included: `Bootstrap: Error starting with plugins enabled`
- `ReflectionTypeLoadException` / `FileLoadException` for `Lidarr.Plugin.Abstractions`
- `AssemblyLoadContext is unloading or was already unloaded`
- Single-plugin load succeeded; **multi-plugin load failed**.

**Root cause:** Host/plugin-loader lifecycle bug (collectible ALC GC/unload) in `PluginLoadContext`.

**Workaround:** CI now auto-enables host override when >1 plugin is tested, mounting a patched
`Lidarr.Common.dll` built from `RicherTunes/Lidarr@fix/pluginloader-keep-load-context`.

## Verified Workaround

Mounting a patched `Lidarr.Common.dll` fixes multi-plugin plugin load on `3.1.1.4884`.

- Patched host source exists locally at `_upstream/Lidarr` on branch `fix/pluginloader-keep-load-context`
- Commit: `1e741479fad766584e196187c61bea302085704a`
- Mount override: `-v <patched>/Lidarr.Common.dll:/app/bin/Lidarr.Common.dll:ro`

## Roadmap

### P0 — Make CI Green (No Host Waiting) ✅ COMPLETED

**Implementation (merged):**
1. Added workflow inputs:
   - `use_host_override: choice` (`auto`, `always`, `never` - default `auto`)
   - `lidarr_override_repo: string` (default `RicherTunes/Lidarr`)
   - `lidarr_override_ref: string` (default pinned SHA `1e741479…`)
2. Auto-detection logic:
   - When `auto`: enables override for >1 plugin, disables for single plugin
   - When `always`: always enables override
   - When `never`: never enables override
3. Workflow steps added:
   - Checkout `RicherTunes/Lidarr` at the fix ref
   - Build `Lidarr.Common.dll` (Release, no analyzers)
   - Mount into container at `/app/bin/Lidarr.Common.dll:ro`
4. Manifest fields added (`lidarr.hostOverride.*`):
   - `used: boolean`
   - `reason: string`
   - `sourceRepo: string`
   - `sourceRef: string`
   - `sha: string`
5. Job summary shows host override status when enabled

**Acceptance Criteria:**
- ✅ Schema gate finds `Qobuzarr` + `Tidalarr` (and `Brainarr` import list) in a single container
- ✅ No `AssemblyLoadContext is unloading` errors
- ✅ Override is explicit in manifest and job summary

### P1 — Make Host Override Self-Validating

Reduce long-term risk by making the override mechanism self-documenting and self-detecting.

- [ ] Add `lidarr.hostOverride.dllSha256` to the manifest (forensics + reproducibility)
- [ ] Add "auto-detect override necessity" probe:
  - Run a fast multi-plugin Schema check once with override off
  - If it fails with ALC signature, re-run with override on
  - Annotate `lidarr.hostOverride.reason="auto-detected-alc-bug"`
- [ ] Add a canary job that runs `override=never` against the moving `pr-plugins` tag with `continue-on-error`
  - Signals when upstream is fixed and override can be removed

### P2 — Speed Up CI Runs

Big win, low risk optimizations.

- [ ] Cache the built `Lidarr.Common.dll` host override artifact keyed by `lidarr_override_ref` (and OS)
  - Avoids rebuilding every run
- [ ] Cache extracted Lidarr assemblies keyed by `lidarr_tag` digest (if not already)

### P3 — Hard Fail on "Host Doesn't Discover Plugins" Mode

Detect when the host silently ignores file-based plugins.

- [ ] Add classifier/error code `E2E_HOST_PLUGIN_DISCOVERY_DISABLED` when:
  - Plugin folders exist in `/config/plugins/...` AND
  - Schemas are missing AND
  - Logs show no plugin load attempt
- [ ] Include "what to do next" in the job summary:
  - Enable host override
  - Pin to known-working tag
  - Install via plugin manager API if Lidarr changes behavior

### P4 — Reduce "Green By Skip" Remaining Holes

Ensure all gates fail explicitly when prerequisites are missing.

- [ ] Ensure strict prereqs apply to canary search-level runs too (if secrets are expected)
- [ ] Move more gate outcomes from regex inference to explicit structured error codes emitted directly by gates

### P5 — Developer Ergonomics / Polishing

Make local development and debugging easier.

- [ ] Add `scripts/validate-manifest.ps1` as a documented local step in `docs/PERSISTENT_E2E_TESTING.md`
- [ ] Add a "How to reproduce this CI run locally" section that prints the exact `e2e-runner.ps1` command + inputs used

---

## Parallelizable Work

Non-overlapping tasks that can be delegated:

| Task | Scope | Dependencies |
|------|-------|--------------|
| Cache host override build + extracted assemblies | Workflow-only | None |
| Add `dllSha256` + override auto-detect probe | Manifest + workflow | Minimal runner touch |
| Add discovery-disabled detection + job-summary hints | Gates/json-output + workflow summary | No plugin repos |

---

## Tracking: Remove Override When Host Fix Lands

1. Track upstream Lidarr PR/commit that fixes loader lifecycle
2. Once hotio publishes a tag containing the fix:
   - Pin workflow `lidarr_tag` to the fixed version/digest
   - Disable host override by default (`use_host_override: never`)
   - Keep override as opt-in for bisecting host regressions

## Non-Goals

- No attempt to “fix” host ALC/GC behavior inside plugins; that’s brittle and likely impossible.
- Do not commit patched Lidarr binaries into this repo.
