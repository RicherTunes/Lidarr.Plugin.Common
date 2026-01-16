# Ecosystem Parity Roadmap

This document tracks progress toward parity (where it makes sense) across the plugin ecosystem (Tidalarr, Qobuzarr, Brainarr, AppleMusicarr) and the shared platform (Lidarr.Plugin.Common).

## Current Status

| Dimension | Tidalarr | Qobuzarr | Brainarr | AppleMusicarr | Common |
|-----------|----------|----------|----------|---------------|--------|
| **Packaging** | ✅ | ✅ | ✅ | ⚠️ | Policy complete |
| **Naming/Path** | ✅ | ✅ | N/A | N/A | FileSystemUtilities |
| **Concurrency** | ✅ | ✅ | N/A | N/A | BaseDownloadOrchestrator |
| **Auth Lifecycle** | ✅ | ✅ | N/A | ⚠️ | Single-authority pattern |
| **Token/Secret Protection** | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ⚠️ (needs stable façade) |
| **Resilience** | ✅ | ✅ | ⚠️ | ✅ | CircuitBreaker (+ WS4.2 parity) |
| **E2E Gates** | ✅ Proven | ✅ Proven | ✅ Schema+ImportList | ⚠️ (schema-only) | Manifest schema + explicit codes |

**Overall Ecosystem Parity: ~97%**

---

## Guiding Rules
- **Thin platform**: any new Common API must delete real duplication in ≤2 follow-up PRs (or be reverted).
- **No silent stubs**: adapters must delegate to real behavior; never ship “empty results” as success.
- **Characterize before refactor**: lock current behavior with tests before rewiring internals.
- **Explicit failures**: E2E/CI should emit structured error codes at the failure site (not inferred from strings).

## Workstreams (Multi-Agent Queue)
This section is intentionally PR-sized and parallelizable. Each work item lists an explicit deletion target (to prevent Common bloat).

| WS | Focus | Primary Repo | Status | Deletion Target (≤2 PRs) |
|----|-------|--------------|--------|---------------------------|
| WS1 | AppleMusicarr correctness + crypto | `applemusicarr/` + `lidarr.plugin.common/` | 🔜 | Delete `DataProtector.cs` + duplicate SecretProtector |
| WS2 | Type-identity: Abstractions distribution | `lidarr.plugin.common/` + all plugins | 🔜 | Remove per-plugin Abstractions compilation path |
| WS3 | Hosting convergence (StreamingPlugin) | `tidalarr/` then `qobuzarr/` | 🔜 | Delete duplicated IPlugin/settings bootstrapping |
| WS4 | Brainarr breaker parity | `lidarr.plugin.common/` + `brainarr/` | 🔜 | Delete Brainarr circuit breaker implementation |
| WS5 | De-dup + drift guardrails | `lidarr.plugin.common/` + plugins | 🔜 | Delete duplicated PreviewDetectionUtility + sanitizers |
| WS6 | Safe-by-default logging + sanitization primitives | `lidarr.plugin.common/` | 🔜 | Delete downstream URL/query redaction copies |
| WS7 | Manifest/tooling coherence | `lidarr.plugin.common/` + `applemusicarr/` | 🔜 | Delete dead/incorrect manifest entrypoints |
| WS8 | CI/workflow parity | `lidarr.plugin.common/` + all plugins | 🔜 | Delete per-repo bespoke submodule/auth steps |

### Coordination
- Claim a work item by replacing `🔜` with an owner handle and PR link (example: `@alex → PR #123`).
- Keep PRs single-purpose: one work item per PR, one repo per PR unless explicitly required.
- If a Common change has no deletion follow-up queued, it violates the thin-platform rule; revert or add the deletion PR immediately.

### WS1: AppleMusicarr correctness + crypto
- [ ] **WS1.1** Fix `manifest.json` entrypoint mismatch for net8 builds (remove dead entryPoint or compile it for net8).
- [ ] **WS1.2** Add `ManifestCheck` optional entrypoint resolution (manifest entryPoints must exist in built assembly).
- [ ] **WS1.3** Migrate AppleMusicarr secret storage to Common stable façade and delete custom crypto (~200+ LOC).

### WS2: Abstractions distribution (byte-identical type identity)
- [ ] **WS2.1** Publish `Lidarr.Plugin.Abstractions` to NuGet.org (requires `NUGET_API_KEY` secret) and document usage.
- [ ] **WS2.2** Migrate plugins from `ProjectReference` → `PackageReference` and delete the local Abstractions compilation path.
- [ ] **WS2.3** Add a packaging tripwire: fail if plugins ship non-identical Abstractions bytes when multi-plugin testing.

### WS3: Hosting convergence (StreamingPlugin)
- [ ] **WS3.1 (Tidalarr)** Move `TidalarrPlugin` to `StreamingPlugin<TidalModule, TidalarrSettings>` and delete duplicated host wiring.
- [ ] **WS3.2 (Qobuzarr)** Only add a modern entrypoint when it delegates to real behavior; never ship stub adapters.

### WS4: Brainarr circuit breaker parity
- [ ] **WS4.1** Characterize existing Brainarr breaker behavior (tests; no code changes).
- [ ] **WS4.2** Implement Common breaker that matches WS4.1 semantics and add parity tests.
- [ ] **WS4.3** Wire Brainarr to Common breaker behind an injected registry seam; delete Brainarr breaker implementation.

### WS5: De-dup + drift guardrails
- [ ] **WS5.1** Expand parity-lint to include AppleMusicarr and new “clone” patterns (PreviewDetectionUtility, sanitizers).
- [ ] **WS5.2** Delete `qobuzarr/src/Utilities/PreviewDetectionUtility.cs` and use Common everywhere.

### WS6: Safe-by-default logging + sanitization primitives
- [ ] **WS6.1** Add safe HTTP logging in `StreamingApiRequestBuilder` (redact or drop query entirely) + tests.
- [ ] **WS6.2** Add reusable sanitization primitives (control/zero-width stripping, whitespace collapse, URL/query redaction helpers).

### WS7: Manifest/tooling coherence
- [ ] **WS7.1** Extend `ManifestCheck.ps1` to validate entrypoints resolve in the built assembly (opt-in, CI-friendly).
- [ ] **WS7.2** Document what `manifest.json` means vs `plugin.json` and converge to one canonical definition where possible.

### WS8: CI/workflow parity
- [ ] **WS8.1** Ensure multi-plugin smoke test fails fast with clear message when `CROSS_REPO_PAT` is missing.
- [ ] **WS8.2** Fix Tidalarr CI “missing Lidarr assemblies” by standardizing host extraction across plugin repos (shared workflow/action).

## Definition of Done

Full ecosystem parity is achieved when:

- [ ] All three plugins ship the 5-DLL type-identity contract
- [ ] Both streaming plugins produce identical filename format on multi-disc and edge sanitization
- [ ] Persistent single-plugin E2E gates pass for Qobuzarr and Tidalarr
- [ ] Multi-plugin schema gate passes for 2 plugins, then 3 plugins (when host supports)

**No-Drift Rule**: Any new filename/path logic must either live in Common or delegate to Common.

---

## Type-Identity Assembly Policy

### Required Assemblies (All Plugins)
Plugins **MUST** ship these assemblies for proper plugin discovery and type identity:
- `Lidarr.Plugin.Abstractions.dll` - Plugin discovery contract
- `Microsoft.Extensions.DependencyInjection.Abstractions.dll` - DI type identity
- `Microsoft.Extensions.Logging.Abstractions.dll` - Logging type identity

### Forbidden Assemblies (All Plugins)
Plugins **MUST NOT** ship these assemblies:
- `System.Text.Json.dll` - Cross-boundary type identity risk
- `Lidarr.Core.dll`, `Lidarr.Common.dll`, `Lidarr.Host.dll` - Host assemblies
- `NzbDrone.*.dll` - Legacy host assemblies

### FluentValidation Exception (Brainarr-Specific)

**Brainarr MUST NOT ship `FluentValidation.dll`.**

Unlike streaming plugins (Qobuzarr, Tidalarr) that don't override validation methods,
Brainarr's `BrainarrImportList` overrides `Test(List<ValidationFailure>)` from `ImportListBase`.

When FluentValidation.dll was shipped:
```
Method 'Test' in type 'Lidarr.Plugin.Brainarr.BrainarrImportList' does not have an implementation.
```

**Root cause**: Override signature mismatch due to FluentValidation type identity.
- Plugin's `ValidationResult` ≠ Host's `ValidationResult` (different ALCs)
- The override signature doesn't match because return types are technically different types
- Plugin must use host's FluentValidation for type identity to match

**Guard test**: `brainarr/Brainarr.Tests/Packaging/BrainarrPackagingPolicyTests.cs:Package_Must_Not_Ship_FluentValidation()`

### ⚠️ Policy Warning: Do Not Force Uniformity

FluentValidation shipping is **plugin-specific**. Do NOT attempt to standardize all plugins
to either ship or not ship FluentValidation.dll without verifying the plugin's override
signatures against the host.

**Before changing FV policy for any plugin:**
1. Check if the plugin overrides `Test(List<ValidationFailure>)` or similar methods
2. If yes, the plugin MUST NOT ship FluentValidation.dll
3. If no overrides, shipping FV is optional (but adds package size)

---

## Phase 1: Lock Contracts with Tests (High Priority)

### 1.1 Packaging Content Tests
- [x] **Common**: PluginPackageValidator with TypeIdentityAssemblies
- [x] **Tidalarr**: PackagingPolicyBaseline updated to 5-DLL contract
- [x] **Qobuzarr**: PackagingPolicyTests with RequiredTypeIdentityAssemblies
- [x] **Brainarr**: BrainarrPackagingPolicyTests with 4-DLL contract (no FluentValidation)

### 1.2 Naming Contract Tests
- [ ] Multi-disc: D01Txx/D02Txx format validation
- [ ] Extension normalization: `.flac` and `flac` both produce `.flac`
- [ ] Unicode normalization: NFC form consistency

### 1.3 Common SHA Verification
- [x] Tidalarr: Uses ext-common-sha.txt
- [x] Qobuzarr: Uses ext-common-sha.txt
- [x] Brainarr: Uses ext-common-sha.txt (fixed format)

---

## Phase 2: Reduce Code Drift (Tech Debt)

### 2.1 Tidalarr Sanitization Consolidation
**Status**: In Progress

Current duplicate paths:
- `TidalDownloadClient.cs:99-100` - FileNameSanitizer for title/artist
- `TidalDownloadClient.cs:405` - FileNameSanitizer for temp file path

**Action**: Route through Common FileSystemUtilities where affecting output filenames.

### 2.2 Qobuzarr Filename Builder Consolidation
**Status**: Complete

- [x] `TrackFileNameBuilder` now delegates to `FileSystemUtilities.CreateTrackFileName`
- [x] Single source of truth for naming/sanitization

### 2.3 Common Internal Audit
**Status**: Pending

Check for direct filename building outside FileSystemUtilities:
- [ ] StreamingPluginMixins
- [ ] DataValidationService
- [ ] Any other helpers creating paths

### 2.4 TidalChunkDownloader Delay Configuration
**Status**: Pending

Current: Fixed `Task.Delay(50)` per chunk (line 49)

Options:
1. Set delay to 0 and rely on rate-limit handlers
2. Make configurable via advanced setting

---

## Auth Lifecycle Hardening (PR2–PR4)

### Single Token Authority Pattern
Both streaming plugins follow a "single token authority" architecture:

| Plugin | Authority Class | Interfaces Implemented |
|--------|-----------------|------------------------|
| Tidalarr | `TidalOAuthService` | ITidalAuth, IStreamingTokenProvider |
| Qobuzarr | `QobuzAuthenticationService` | IQobuzAuthenticationService, IStreamingTokenProvider |

**Invariants (tested via characterization tests)**:
- One class implements all auth interfaces (no adapters/wrappers)
- Expected lifetime is single instance per container (DI same-instance test)
- No competing token providers (Tidalarr PR2)

### Token Storage Patterns

| Plugin | Storage | Fallback | TTL |
|--------|---------|----------|-----|
| Tidalarr | `FileTokenStore` (ConfigPath) | `FailOnIOTokenStore` (throws) | Persistent |
| Qobuzarr | `ICacheManager` (in-memory) | N/A | 24 hours |

**Key difference**: Tidalarr uses file-based OAuth tokens; Qobuzarr uses in-memory session cache.
- Qobuzarr has no file persistence risk
- Tidalarr's `FailOnIOTokenStore` prevents silent writes to temp directory when ConfigPath not set

**Note**: If Lidarr's host DI lifetime changes, the DI characterization tests should be updated accordingly.

---

## Phase 3: Persistent E2E Gates

### 3.1 Gate Definitions

| Gate | Level | Credentials | What It Proves |
|------|-------|-------------|----------------|
| **Schema** | 0 | None | Plugin loaded, indexer/downloadclient/importlist schemas registered |
| **Config/Auth** | 1 | Required | `POST indexer/test` passes (plugin's auth/config validation works) |
| **ReleaseSearch** | 2 | Required | `AlbumSearch` command → `/api/v1/release?albumId=` returns results |
| **Grab** | 3 | Required | Release grabbed → file appears in /downloads |

**Current implementation**: All gates (0-3) are implemented and passing for streaming plugins.

### 3.2 E2E Runner
**Location**: `lidarr.plugin.common/scripts/e2e-runner.ps1`

Features:
- Per-plugin configuration (search queries, credential field names)
- Credential skip semantics (graceful skip when creds missing)
- URL/token redaction in diagnostics
- Cascade skip (Grab skipped when Search skipped)

**E2E Gate Cascade (current)**:
Schema → Configure → Search → AlbumSearch → Grab → Persist → Revalidation

**Optional Gates**:
- ImportList (Brainarr)
- Metadata (opt-in via `-ValidateMetadata`)
- PostRestartGrab (opt-in via `-PostRestartGrab`)

**PR #187 Enhancements** (merged):
- JSON Schema validation: `manifest.schema.json` with `$schema` fetchable pinning
- Job summary output for GitHub Actions with per-plugin pass/fail table
- Expanded gate granularity with Revalidation gate

### 3.3 Plugin-Specific Status

| Plugin | Schema | Configure | Search | AlbumSearch | Grab | Persist | Revalidation |
|--------|--------|-----------|--------|-------------|------|---------|--------------|
| Tidalarr | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Qobuzarr | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Brainarr | ✅ | N/A | N/A | N/A | N/A | N/A | N/A |

| Plugin | ImportList | BrainarrLLM |
|--------|------------|-------------|
| Brainarr | ⏭️/✅ (config-dependent) | ⏭️/✅ (opt-in) |

**3-Plugin Coexistence**: ✅ All three plugins load simultaneously in same Lidarr instance.

### 3.4 Multi-Plugin E2E Command
```bash
# Schema gate (no credentials required):
./e2e-runner.ps1 -Plugins 'Qobuzarr,Tidalarr,Brainarr' -Gate schema \
    -LidarrUrl 'http://localhost:8691' \
    -ExtractApiKeyFromContainer -ContainerName 'lidarr-multi-plugin-persist'

# All gates (skips gracefully when creds missing):
./e2e-runner.ps1 -Plugins 'Qobuzarr,Tidalarr,Brainarr' -Gate all \
    -LidarrUrl 'http://localhost:8691' \
    -ApiKey '<key>'
```

---

## Phase 4: Advanced E2E Gates

### 4.1 ReleaseSearch Gate (Level 2)
✅ Implemented (AlbumSearch + release attribution diagnostics)

### 4.2 Grab Gate (Level 3)
✅ Implemented (queue polling + audio validation + optional metadata gate)

### 4.3 3-Plugin Concurrent Operation
**Status**: ✅ Proven (best-effort; see caveat)

All three plugins can coexist in the same Lidarr instance and pass Schema gate simultaneously

#### ⚠️ Multi-Plugin Stability Caveat

**Port :8691 is "best-effort" until Lidarr AssemblyLoadContext fix.**

Multi-plugin testing on a shared Lidarr instance (e.g., `:8691`) may exhibit intermittent failures due to an upstream Lidarr ALC lifecycle bug. Known symptoms:
- Plugin schemas occasionally missing after restart
- Type identity errors when multiple plugins reference shared types
- Non-deterministic test failures in CI

**Recommendation**:
- Use dedicated single-plugin instances (`:8690` Tidalarr, `:8692` Qobuzarr) for reliable E2E
- Treat `:8691` multi-plugin results as informational, not blocking
- Track upstream: [Lidarr ALC issue](https://github.com/Lidarr/Lidarr/issues) (pending link)

---

## Quick Reference: File Locations

### Packaging Tests
- `tidalarr/tests/Tidalarr.Tests/Unit/Packaging/PackagingPolicyBaseline.cs`
- `qobuzarr/tests/Qobuzarr.Tests/Compliance/PackagingPolicyTests.cs`
- `brainarr/Brainarr.Tests/Packaging/BrainarrPackagingPolicyTests.cs`
- `lidarr.plugin.common/tests/PackageValidation/PluginPackageValidator.cs`

### Filename Utilities
- `lidarr.plugin.common/src/Utilities/FileSystemUtilities.cs`
- `qobuzarr/src/Utilities/TrackFileNameBuilder.cs` (delegates to Common)
- `tidalarr/src/Tidalarr/Integration/TidalDownloadClient.cs` (needs consolidation)

### E2E Scripts
- `lidarr.plugin.common/scripts/e2e-runner.ps1` - Main gate runner
- `lidarr.plugin.common/scripts/lib/e2e-gates.psm1` - Gate implementations
- `lidarr.plugin.common/scripts/lib/e2e-diagnostics.psm1` - Diagnostics bundle

---

## Changelog

| Date | Change |
|------|--------|
| 2025-12-31 | Common PR #187: JSON Schema + $schema fetchable pinning + job summary |
| 2025-12-31 | Qobuzarr PR4: Dead code deletion + 8 auth characterization tests (incl. DI same-instance) |
| 2025-12-31 | Tidalarr PR3: TidalOAuthService fallback → FailOnIOTokenStore (no silent temp writes) |
| 2025-12-31 | Tidalarr PR2: Auth lifecycle unification - single token authority, scoped IStreamingTokenProvider |
| 2025-12-31 | E2E bootstrap validation: All gates pass for Tidalarr (Schema→Persist→Revalidation) |
| 2025-12-31 | Added Auth Lifecycle Hardening section documenting single-authority pattern |
| 2025-12-30 | Added multi-plugin stability caveat (:8691 best-effort until Lidarr ALC fix) |
| 2025-12-30 | PR #186: SimpleDownloadOrchestrator metadata tagging + ILogger + fail-fast |
| 2025-12-27 | Brainarr PR #346: FluentValidation exclusion fix with guard test |
| 2025-12-27 | Added Type-Identity Assembly Policy section with FluentValidation exception |
| 2025-12-27 | E2E gates: credential skip semantics, indexer/test preference, URL redaction |
| 2025-12-27 | 3-plugin coexistence proven: Qobuzarr, Tidalarr, Brainarr all pass Schema gate |
| 2025-12-27 | Tidalarr PR #104: sanitization consolidation with 4 unit tests |
| 2025-12-27 | Tidalarr PR #105: chunk delay configurability with clamping (0-2000ms) |
| 2025-12-27 | Common PRs #167/#168 merged - packaging policy complete |
| 2025-12-27 | Tidalarr packaging baseline updated to 5-DLL contract |
| 2025-12-27 | Qobuzarr TrackFileNameBuilder delegated to Common |
| 2025-12-27 | Initial roadmap created |
