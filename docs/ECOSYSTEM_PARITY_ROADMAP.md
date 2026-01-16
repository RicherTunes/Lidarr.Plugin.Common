# Ecosystem Parity Roadmap

This document tracks progress toward full structural and behavioral parity across the plugin ecosystem (Tidalarr, Qobuzarr, Brainarr, AppleMusicarr).

## Current Status

| Dimension | Tidalarr | Qobuzarr | Brainarr | AppleMusicarr | Common |
|-----------|----------|----------|----------|---------------|--------|
| **Packaging** | ✅ | ✅ | ✅ | ⚠️ (WS1) | Policy complete |
| **Naming/Path** | ✅ | ✅ | N/A | N/A | FileSystemUtilities |
| **Concurrency** | ✅ | ✅ | N/A | N/A | BaseDownloadOrchestrator |
| **Auth Lifecycle** | ✅ (PR2/PR3) | ✅ (PR4) | N/A | ⚠️ (WS1) | Single-authority pattern |
| **Token/Secret Protection** | ⚠️ | ⚠️ | ⚠️ | ⚠️ | `ISecretProtector` facade (PR #280) |
| **Resilience** | ✅ | ✅ | ⚠️ | ✅ | AdvancedCircuitBreaker (PR #283) |
| **E2E Gates** | ✅ Proven | ✅ Proven | ✅ Schema+ImportList | ⚠️ (WS1) | Manifest schema + contracts |

**Streaming parity (Tidalarr + Qobuzarr): ~99%**

**Ecosystem parity (incl. Brainarr + AppleMusicarr): ~90-95%**

---

## Status Board (Workstreams)

Rule: Any new `lidarr.plugin.common/` API must delete measurable duplication in at least one plugin within 1-2 follow-up PRs (or it does not land).

| WS | Goal | Status | Dependency | Next PR-sized step |
|----|------|--------|------------|--------------------|
| WS1 | AppleMusicarr: secret protection convergence | Blocked | Common PR #280 (`ISecretProtector`) | Merge #280 → migrate AppleMusicarr secrets to façade → delete AppleMusicarr legacy secret wrappers |
| WS2 | Byte-identical Abstractions distribution | Blocked on secret | `NUGET_API_KEY` in Common | Publish `Lidarr.Plugin.Abstractions` to NuGet + convert at least 1 plugin to `PackageReference` |
| WS3 | Hosting convergence (streaming plugins) | In progress | None | Tidalarr: `StreamingPlugin<>` migration + deletion; Qobuzarr: no stub adapters (hard stop) |
| WS4 | Brainarr resilience convergence | In progress | Common PR #283 + Brainarr PRs #371/#372 | Merge #371/#372/#283 → adapter registry → swap to Common breaker → delete old breaker |
| WS5 | No-drift guardrails | In progress | Common PR #284 + Qobuzarr PR #156 | Merge #284/#156 → extend parity-lint rules only when paired with a deletion PR |
| WS6 | CI/workflow standardization | In progress | Common reusable workflows | Merge Common PR #281 → propagate to plugin repos; document required `CROSS_REPO_PAT` secret |
| WS7 | HTTP safe-by-default logging | Done | Common #273 | Add regression tests + delete any remaining per-plugin URL redaction forks |
| WS8 | Manifest/entrypoint tooling | Pending | None | Add opt-in entrypoint type-resolution check (net8) to `tools/ManifestCheck.ps1` |

### Multi-Agent Queue (Weeks of Parallel Work)

Each item below is intended to be an independent, PR-sized unit. Avoid overlapping edits to the same “high-churn” files (especially `scripts/e2e-runner.ps1` and `scripts/lib/e2e-gates.psm1`).

| Lane | PR-sized unit | Definition of Done |
|------|---------------|--------------------|
| A | Merge Common PR #280 (`ISecretProtector`) | CI green; a deletion follow-up PR is queued (AppleMusicarr) |
| A | AppleMusicarr: migrate secrets to `ISecretProtector` + delete legacy wrappers | Removes AppleMusicarr secret wrapper glue; proves legacy values still decrypt |
| B | Merge Brainarr PR #371 (breaker characterization tests) | CI green; tests become the migration contract |
| B | Merge Brainarr PR #372 (injectable `IBreakerRegistry` seam) | Characterization tests still pass unchanged |
| B | Merge Common PR #283 (AdvancedCircuitBreaker) | Common tests pass; API baseline updated |
| B | Brainarr: adapter registry + swap to Common breaker | Characterization tests pass unchanged, now backed by Common |
| B | Brainarr: delete old breaker implementation | No Brainarr breaker implementation remains; characterization tests still pass |
| C | Merge Common PR #284 (parity-lint includes applemusicarr) | CI green; parity-lint runs in CI and stays quiet on baseline |
| C | Merge Qobuzarr PR #156 (delete duplicate PreviewDetectionUtility) | Local clone removed from `origin/main`; tests pass |
| C | Common: parity-lint rule for PreviewDetectionUtility clone | Lint fails when clone reintroduced; requires a paired deletion PR |
| D | Abstractions: publish `Lidarr.Plugin.Abstractions` to NuGet.org | Tag+package published; doc shows how plugins consume it |
| D | Switch 1 plugin to `PackageReference` Abstractions | Packaging passes; no Abstractions recompilation in that plugin |
| E | Merge Common PR #281 (`CROSS_REPO_PAT` fail-fast + fork skip) | Multi-plugin smoke test fails fast with actionable message |
| E | Tidalarr: fix “missing Lidarr assemblies” CI (unblock PR #127) | CI can build without local Lidarr assemblies; no behavior change |
| F | ManifestCheck: opt-in entrypoint type-resolution check | Fails CI when manifest references missing types; has a broken fixture test |

---

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

**E2E runner platform (merged)**:
- Manifest output is schema-validated and contract-guarded (golden fixtures + doc-sync tripwires)
- Explicit-at-source `errorCode` values for high-frequency failures (no inference-by-string)
- Host capability probing and source provenance recorded in the manifest

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

## Phase 4: Optional / Advanced E2E Gates

These gates exist to tighten regressions; they are opt-in due to flake cost and credential requirements.

- **Metadata**: validates core tags after Grab (`-ValidateMetadata`)
- **PostRestartGrab**: proves end-to-end after Persist restart (`-PostRestartGrab`)
- **BrainarrLLM**: opt-in functional gate for remote/local LLM endpoints (`-RunBrainarrLLMGate`)

### Multi-Plugin Operation
**Status**: ✅ Proven for Schema + streaming Grab (host constraints vary by image tag)

All three plugins can coexist in the same Lidarr instance and pass Schema gate simultaneously.

#### ⚠️ Multi-Plugin Stability Caveat

Multi-plugin results are sensitive to host AssemblyLoadContext behavior. The runner records host capability evidence in the manifest (ALC fix present/not present).

Known symptoms when the host is affected:
- Plugin schemas occasionally missing after restart
- Type identity errors when multiple plugins reference shared types
- Non-deterministic test failures in CI

**Recommendation**:
- Prefer dedicated single-plugin instances for OAuth and focused debugging
- Use the multi-plugin harness for coexistence regression detection
- Track upstream host fix: `Lidarr/Lidarr#5662` (ALC load-context lifetime)

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
