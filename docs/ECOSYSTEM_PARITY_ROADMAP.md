# Ecosystem Parity Roadmap

This document tracks progress toward full structural and behavioral parity across the plugin ecosystem (Tidalarr, Qobuzarr, Brainarr, AppleMusicarr).

## Current Status

| Dimension | Tidalarr | Qobuzarr | Brainarr | AppleMusicarr | Common |
|-----------|----------|----------|----------|---------------|--------|
| **Packaging** | ✅ | ✅ | ✅ | ✅ | Policy complete |
| **Canonical Abstractions** | ✅ | ✅ | ✅ | ✅ | v1.5.0 pinned |
| **Manifest Entrypoints** | ✅ | ✅ | ✅ | ✅ | -ResolveEntryPoints |
| **Naming/Path** | ✅ | ✅ | N/A | N/A | FileSystemUtilities |
| **Concurrency** | ✅ | ✅ | N/A | N/A | BaseDownloadOrchestrator |
| **Auth Lifecycle** | ✅ | ✅ | N/A | Custom | Single-authority pattern |
| **Token Protection** | ✅ | ✅ | N/A | Custom | See note below |
| **E2E Gates** | ✅ Proven | ✅ Proven | ✅ Schema+ImportList | ⚠️ Metadata-only | JSON schema |

**Overall Ecosystem Parity: ~95%**

### AppleMusicarr Notes

AppleMusicarr is a **metadata-only** plugin (no audio downloads) with different characteristics:
- **Naming/Path**: Not applicable (no file downloads)
- **Concurrency**: Not applicable (no download orchestration)
- **Auth Lifecycle**: Uses Apple Music API authentication (different from OAuth2 PKCE)
- **Token Protection**: Custom implementation in `AppleMusicSecretProtection.cs` (migration target: Common facade)
- **E2E Gates**: Appropriate gates are schema validation and import list sync (no download/grab gates)

---

## Definition of Done

Full ecosystem parity is achieved when:

- [x] All four plugins use canonical Abstractions with SHA256 verification
- [x] All four plugins validate manifest entrypoints via -ResolveEntryPoints
- [ ] All four plugins enforce non-negotiable CI gates via the reusable workflow in `lidarr.plugin.common` (`.github/workflows/packaging-gates.yml`)
- [ ] Both streaming plugins (Qobuzarr, Tidalarr) produce identical filename format on multi-disc and edge sanitization
- [ ] AppleMusicarr token protection migrated to Common facade (dual-read, new-write)
- [ ] Persistent single-plugin E2E gate is enforced in CI for Qobuzarr and Tidalarr (Schema gate required; higher gates opt-in with credentials)
- [ ] Multi-plugin schema gate passes for 2+ plugins (when host supports)

**No-Drift Rule**: Any new filename/path logic must either live in Common or delegate to Common.

---

## Type-Identity Assembly Policy

### Required Assemblies (All Plugins)
Plugins **MUST** ship these assemblies in the plugin payload:
- `Lidarr.Plugin.Abstractions.dll` - Canonical ABI contract (all plugins must ship identical bytes; enforced by `tools/canonical-abstractions.json`)

### Host-Shared Assemblies (Do Not Ship)
Plugins **MUST NOT** ship these assemblies (host provides them; shipping causes cross-ALC type identity breakage):
- `Microsoft.Extensions.DependencyInjection.Abstractions.dll` - breaks `IServiceProvider`/DI contracts
- `Microsoft.Extensions.Logging.Abstractions.dll` - breaks `ILogger` contracts

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
- [x] **Common**: `tools/PluginPack.psm1` enforces the DLL cleanup/merge policy (packages must ship the plugin DLL + canonical `Lidarr.Plugin.Abstractions.dll`; non-DLL artifacts like `plugin.json`, `manifest.json`, `.lidarr.plugin` may also be present depending on plugin/host needs)
- [ ] **All Plugins**: Packaging tests assert the Common policy (and fail on host-shared assemblies in the ZIP)

### 1.2 Naming Contract Tests
- [x] **Qobuzarr**: Multi-disc prefix + extension mapping + NFC normalization (`qobuzarr/src/Utilities/TrackFileNameBuilder.cs`, `qobuzarr/tests/Qobuzarr.Tests/Unit/Utilities/TrackFileNameBuilderTests.cs`)
- [x] **Tidalarr**: Multi-disc prefix + extension mapping + NFC normalization (`tidalarr/src/Tidalarr/Integration/TidalDownloadClient.cs`, `tidalarr/tests/Tidalarr.Tests/TidalDownloadClientFileNameTests.cs`)
- [x] **Common**: Extracted `FileNameAssertions.cs` into `testkit/Assertions/` with shared contract helpers (NFC normalization, invalid chars, reserved names, multi-disc prefix, extension validation)

### 1.3 Common SHA Verification
- [x] Tidalarr: Uses ext-common-sha.txt
- [x] Qobuzarr: Uses ext-common-sha.txt
- [x] Brainarr: Uses ext-common-sha.txt (fixed format)

---

## Phase 2: Reduce Code Drift (Tech Debt)

### 2.0 Blockers / Drift Dragons (Must Fix)

- [x] **Brainarr**: Remove committed local state (`brainarr/.worktrees/`, `brainarr/_plugins/`) and add `.gitignore` + CI guard (commit 09fef6c).
- [x] **Brainarr**: Fix `brainarr/build.ps1` corruption (was failing at `brainarr/build.ps1:56` and `brainarr/build.ps1:180`) and add CI "parse check" step (`brainarr/.github/workflows/sanity-build.yml`).
- [x] **Brainarr**: Align `brainarr/.github/workflows/packaging-closure.yml` with packaging policy (commit 1d0144e - now uses unified `New-PluginPackage` pipeline).
- [x] **AppleMusicarr**: Fix non-portable submodule URL (commit 4b171cc - `.gitmodules` now uses HTTPS URL for `ext/AppleMusiSharp`).
- [x] **AppleMusicarr**: Removed legacy packaging script (commit 7912a09 - deleted `scripts/pack-plugin.ps1`, CI now uses `build.ps1 Release -Package`).
- [x] **Qobuzarr**: Treat `AppSecret` as a secret in UI (commit 45c4d3f - now uses `FieldType.Password` + `PrivacyLevel.Password`).
- [x] **Common**: Make `New-PluginPackage -MergeAssemblies` deterministic (commit 06e476e - added fail-fast guard when ilrepack missing; no one uses this flag so effectively deprecated).

**Note**: These are parity items because they cause drift, make local builds non-reproducible, or produce packages that can diverge from the canonical policy.

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
**Status**: Complete

Current: Configurable per-chunk delay with default `0` (max speed). Socket-leak-safe chunk downloads.

Options:
1. Set delay to 0 and rely on rate-limit handlers
2. Make configurable via advanced setting

### 2.5 Download Parity (Streaming) (PR1-PR3)
**Status**: Planned

This slice targets the remaining, measurable performance gap between streaming plugins while keeping `lidarr.plugin.common` "thin" (primitives, not provider policy).

#### 2.5.1 Common: Streaming-friendly retry (`HttpCompletionOption`)
**Goal**: Allow plugins to use Common retry logic while streaming large payloads efficiently.

- [ ] Add `ExecuteWithRetryAsync(..., HttpCompletionOption, ...)` overload (keep existing overload behavior unchanged).
- [ ] Add a test proving `ResponseHeadersRead` returns promptly vs buffered reads.

**Fix location**: `lidarr.plugin.common/src/Utilities/HttpClientExtensions.cs`

**Acceptance criteria**:
- Existing call sites compile and behave unchanged.
- New test demonstrates `ResponseHeadersRead` does not force full buffering before returning.

#### 2.5.2 Tidalarr: File-backed stream provider + controlled track concurrency
**Goal**: Enable memory-safe parallelism (default remains serial) and avoid in-memory track assembly.

- [ ] Manifest-path chunk assembly returns a file-backed stream (use `FileOptions.DeleteOnClose` lifecycle, like the legacy stream-info path).
- [ ] Add `MaxConcurrentTrackDownloads` (advanced) with safe bounds; default `1`.
- [ ] Pass `maxConcurrentTracks` into `SimpleDownloadOrchestrator` from `TidalModule`.
- [ ] Adopt `ResponseHeadersRead` for chunk downloads via the Common overload from 2.5.1.

**Fix locations**:
- `tidalarr/src/Tidalarr/Integration/TidalChunkStreamProvider.cs`
- `tidalarr/src/Tidalarr/Domain/Streaming/TidalChunkDownloader.cs`
- `tidalarr/src/Tidalarr/Integration/TidalModule.cs`

**Acceptance criteria** (manual / local perf verification):
- With `MaxConcurrentTrackDownloads=2` and `DownloadDelay=0`, a 10-track album download completes in < 120s on a typical broadband connection (or shows >= 2x speedup vs `MaxConcurrentTrackDownloads=1` on the same machine/network).
- With `MaxConcurrentTrackDownloads=2`, process/container RSS remains < 350MB during a 10-track album download and does not monotonically grow across multiple albums.

#### 2.5.3 Tidalarr: Re-enable OAuth state validation
**Goal**: Security hardening + fewer stale/cross-tab auth failures now that ConfigPath defaults are stable.

- [ ] Validate callback `state` matches stored `PKCEState.State` and fail with a clear user-facing message on mismatch.
- [ ] On mismatch, regenerate OAuth URL (do not silently proceed).
- [ ] Add unit tests for (a) mismatch, (b) missing state, (c) happy path.

**Fix locations**:
- `tidalarr/src/Tidalarr/Integration/LidarrNative/TidalLidarrIndexer.cs`
- `tidalarr/src/Tidalarr/Infrastructure/Storage/PKCEStateStore.cs` (if required for clean access)

**Acceptance criteria**:
- Happy path OAuth flow still works.
- Stale/cross-tab OAuth flow fails deterministically with actionable guidance and no secret leakage in logs.

#### 2.5.4 Follow-up: Adopt streaming retry overload in other plugins (2.5.1 rollout)
**Goal**: Ensure all plugins benefit from 2.5.1 when they have streaming/large-payload HTTP paths.

- [ ] Qobuzarr: audit for any Common retry usage on large responses and adopt `ResponseHeadersRead` where appropriate.
- [ ] Brainarr: audit for any large streaming downloads (if any) and adopt the overload where applicable.

**Acceptance criteria**:
- No functional change (behavior-preserving), only reduced buffering/peak memory where applicable.

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

**PR #187 Enhancements** (pending merge):
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

## Phase 4: Advanced E2E Gates (Future)

### 4.1 ReleaseSearch Gate (Level 2)
**Not yet implemented**

Design:
1. Create temporary album via `POST /api/v1/album`
2. Trigger `AlbumSearch` command
3. Assert `/api/v1/release?albumId=` contains results with plugin's indexerId
4. Clean up temporary album

### 4.2 Grab Gate (Level 3)
**Not yet implemented**

Design:
1. Pick deterministic release from ReleaseSearch results
2. Trigger grab via `POST /api/v1/release`
3. Assert queue contains item
4. Assert file appears in /downloads (timeout with polling)

### 4.3 3-Plugin Concurrent Operation
**Status**: ✅ Proven for Schema gate

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

## Test Suite Hygiene & Verification

### verify-merge-train.ps1 Flags

The merge train script supports optional flags to skip test categories that require special prerequisites:

| Flag | Purpose | When to Use |
|------|---------|-------------|
| `-SkipIntegration` | Skips tests with `Category=Integration` | When env vars (API keys, credentials) are not available |
| `-SkipPerformance` | Skips tests with `Category=Performance` | When running in Docker/CI where wall-clock assertions are unreliable |
| `-Docker` | Runs tests in Docker container | For reproducible, isolated verification |

**Example: Full Docker verification without secrets**
```powershell
./verify-merge-train.ps1 -Docker -Mode full -SkipIntegration -SkipPerformance
```

**Expected outcome**: Unit tests must pass; integration/performance are opt-in.

### Smoke Test Behavior (Multi-Plugin)

The multi-plugin smoke test (`multi-plugin-smoke-test.yml`) requires `CROSS_REPO_PAT` to clone plugin repositories.

| Condition | Behavior |
|-----------|----------|
| `CROSS_REPO_PAT` set | Full smoke test runs across all plugins |
| `CROSS_REPO_PAT` missing | Job skips with notice: "CROSS_REPO_PAT not available, skipping smoke test" |

**To enable**: Set `CROSS_REPO_PAT` as a repository secret with `repo` scope access to all plugin repos.

**PR #295**: Made smoke test non-blocking when secret is missing.

### Integration Test Quarantine Policy

Tests requiring external prerequisites (API credentials, live services) **MUST self-skip** when those prerequisites are unavailable.

**Required Pattern** (xUnit with SkippableFact):
```csharp
[SkippableFact]
[Trait("Category", "Integration")]
public async Task MyLiveApiTest()
{
    Skip.If(_client == null,
        "Qobuz credentials not configured (set QOBUZ_APP_ID, QOBUZ_EMAIL, QOBUZ_PASSWORD)");

    // ... test code
}
```

**Policy requirements**:
1. Use `[SkippableFact]` or `[SkippableTheory]` from `Xunit.SkippableFact` package
2. Call `Skip.If()` at test start with condition and actionable message
3. Skip message **MUST list required environment variables** by name
4. Tag test with `[Trait("Category", "Integration")]` for filter support
5. No behavior change when prerequisites are present

**Plugin-specific implementations**:
| Plugin | File | Status |
|--------|------|--------|
| Qobuzarr | `QobuzDownloadClientIntegrationTests.cs` | ✅ PR #164 |
| Brainarr | Tests use `[Trait("Category", "Performance")]` | ✅ PR #294 |
| Tidalarr | TBD | Pending audit |

### Docker Full Mode Expectations

When running `verify-merge-train.ps1 -Docker -Mode full`:

| Test Category | Expectation | Skip Flag |
|---------------|-------------|-----------|
| Unit tests | **MUST pass** | N/A |
| Integration tests | Should skip (no credentials) | `-SkipIntegration` |
| Performance tests | Should skip (wall-clock unreliable) | `-SkipPerformance` |

**Clean Docker run = 0 failures, some skips expected.**

Tests that fail in Docker without prerequisites are considered bugs and must be fixed to self-skip.

---

## XP Series: Cross-Platform Test Parity

The XP series tracks issues where tests pass on Windows but fail on Linux/Docker.

| ID | Issue | Status | Fix Location | PR |
|----|-------|--------|--------------|-----|
| XP1 | URI canonicalization: `/v1/endpoint` parsed as `file:///` on Linux | ✅ Merged | AppleMusicarr `BuildRequestUri()` | [#23](https://github.com/RicherTunes/AppleMusicarr/pull/23) |
| XP2 | (Reserved) | - | - | - |
| XP3 | Reserved name sanitization tests | ✅ Done | - | - |
| XP4 | AppleMusicarr test hygiene (hermetic mocks, skip semantics) | ✅ Merged | AppleMusicarr tests | [#22](https://github.com/RicherTunes/AppleMusicarr/pull/22) |
| XP5 | Brainarr flaky top-up test (non-atomic counter + brittle mock) | ✅ Merged | Brainarr tests | [#387](https://github.com/RicherTunes/Brainarr/pull/387) |

### XP1: Cross-Platform URI Canonicalization

**Root Cause**: `Uri.TryCreate("/v1/endpoint", UriKind.Absolute)` behaves differently:
- **Windows**: Returns `FALSE` → path treated as relative ✅
- **Linux**: Returns `TRUE` → `file:///v1/endpoint` ❌

**Fix**: Add scheme check in `AppleMusicApiClient.BuildRequestUri()`:
```csharp
if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) &&
    (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
```

**Acceptance Tests**: [Common#305](https://github.com/RicherTunes/Lidarr.Plugin.Common/pull/305) - 18 tests verifying Common library is cross-platform safe.

### XP4: AppleMusicarr Test Hygiene (PR #22)

**Issues Fixed**:
- `[SkippableFact]` + `Skip.If()` for proper skip reporting (not `return;`)
- Deterministic cache eviction: sort by (AtMs, then Key) for tie-breaking
- `GatedHandler` pattern for dedup test: barrier-based synchronization, no timing hacks
- `[Collection("TextCacheIsolated")]` for cache test isolation
- CatalogId test fix: use 10-digit ID to match `CatalogIdRegex` pattern

### XP5: Brainarr Flaky Top-Up Test (PR #387)

**Root cause**: Flaky test, not algorithm bug
- Non-atomic counter: `providerCalls++` → race condition
- Brittle mock: exact call-count dependent (call 2 → Phoebe, call 3+ → empty)

**Fix**:
- `Interlocked.Increment(ref providerCalls)` for thread-safety
- Mock changed: call 1 → {Arctic+Lana}, all subsequent → {Phoebe} (robust to extra internal calls)

**Verification**: 0 fails / 50 runs (was 2 fails / 20 runs before fix)

---

## Changelog

| Date | Change |
|------|--------|
| 2026-01-19 | XP1 fixed: file:// URI on Linux; AppleMusicarr#23 merged |
| 2026-01-19 | XP5 fixed + Docker verified: Brainarr top-up test flakiness (PR #387) |
| 2026-01-19 | XP4 complete: AppleMusicarr test hygiene (PR #22) |
| 2026-01-18 | Added Test Suite Hygiene section: verify-merge-train flags, smoke test behavior, integration quarantine policy |
| 2025-12-31 | Common PR #187: JSON Schema + $schema fetchable pinning + job summary (pending merge) |
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
