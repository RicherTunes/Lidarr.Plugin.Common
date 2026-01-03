# E2E Stabilization Roadmap (Multi-Plugin)

## Current Goal

Make the GitHub Actions workflow `E2E Bootstrap (Headless)` deterministic and green for:

- `Qobuzarr` + `Tidalarr` streaming E2E: Schema → Search → AlbumSearch → Grab (+ optional gates)
- `Brainarr` import list E2E: Schema → ImportList (+ optional LLM proof gate)
- **Multi-plugin coexistence** in a single Lidarr container

## Known Host Behaviors (Important)

1. **File-based plugin discovery works on** `ghcr.io/hotio/lidarr:pr-plugins-3.1.1.4884`
   - Plugins placed at `/config/plugins/<Vendor>/<PluginName>/` are loaded.
2. **File-based plugin discovery does not work on** `ghcr.io/hotio/lidarr:pr-plugins` (current moving tag, observed `3.1.1.4901`)
   - `/api/v1/system/plugins` returns `[]` and indexer/download client schemas do not include plugins.
   - Treat this as a host behavior change; do not assume folder scanning.

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

### P1 — Make Failures Actionable

1. Improve error classification in manifests:
   - Distinguish `HOST_ALC_BUG` vs `ABI_MISMATCH` vs `DEPENDENCY_DRIFT`.
2. Add a “plugin discovery mode” diagnostic:
   - If plugin folders exist but schemas missing, fetch `/api/v1/system/plugins` to determine host mode.
3. Add a single-page troubleshooting doc:
   - Common failure signatures → remediation steps.

### P2 — Remove the Workaround (When Host Fix Lands)

1. Track upstream Lidarr PR/commit that fixes loader lifecycle.
2. Once hotio publishes a tag containing the fix:
   - Pin workflow `lidarr_tag` to the fixed version/digest.
   - Disable host override by default.
   - Keep override as an opt-in for bisecting host regressions.

### P3 — Standardization / Polish

1. Ensure all plugins emit zips in consistent locations (already mostly standardized):
   - Qobuzarr: `qobuzarr/artifacts/packages/*.zip`
   - Tidalarr: `tidalarr/src/Tidalarr/artifacts/packages/*.zip`
   - Brainarr: align to `brainarr/artifacts/packages/*.zip` (or document divergence)
2. Standardize vendor folder and manifest checks:
   - `/config/plugins/RicherTunes/<PluginName>/plugin.json` must exist.
3. Add caching where safe:
   - Lidarr assemblies extraction cache (by image digest)
   - Plugin package cache (by commit SHA) for workflow_dispatch runs

## Non-Goals

- No attempt to “fix” host ALC/GC behavior inside plugins; that’s brittle and likely impossible.
- Do not commit patched Lidarr binaries into this repo.
