# Ecosystem Parity Roadmap

This document tracks progress toward full structural and behavioral parity across the plugin ecosystem (Tidalarr, Qobuzarr, Brainarr, AppleMusicarr).

## Current Status

| Dimension | Tidalarr | Qobuzarr | Brainarr | AppleMusicarr | Common |
|-----------|----------|----------|----------|---------------|--------|
| **Packaging Policy** | ✅ | ✅ | ✅ | ✅ | Policy + validators |
| **Type Identity** | ✅ | ✅ | ✅ (FV exception) | ✅ | Canonical contract |
| **Naming/Path** | ⚠️ (some drift) | ✅ | N/A | N/A | FileSystemUtilities |
| **Auth/Secrets** | ✅ | ✅ | N/A | ⚠️ (migration in progress) | Secret protection primitives |
| **Manifest Tooling** | ✅ | ✅ | ✅ | ⚠️ (entrypoints) | ManifestCheck contract |
| **Multi-Plugin Smoke** | ⚠️ (needs secrets) | ⚠️ (needs secrets) | ⚠️ (needs secrets) | N/A | Reusable workflow |
| **E2E Gates** | ✅ Proven | ✅ Proven | ✅ Schema+ImportList | N/A | JSON schema + explicit error codes |

**Overall Ecosystem Parity: ~97% (blocked mostly by CI/secrets + a few remaining drift deletions)**

---

## Blockers (External)

- **GitHub Actions billing/spend**: blocks CI execution and required checks on PRs.
- **`CROSS_REPO_PAT`**: required for multi-plugin smoke workflow in caller repos (Qobuzarr/Tidalarr/Brainarr).
- **Upstream Lidarr ALC lifecycle bug**: multi-plugin runs remain best-effort until host fix is shipped in a published image.

---

## Merge Playbook (When Billing Unblocks)

This section provides exact steps to land the current PR stack safely.

### Phase 1: Common PRs (merge in order)

```bash
# 1. ManifestCheck -ResolveEntryPoints (enables flag AppleMusicarr uses)
gh pr merge 290 --squash --repo RicherTunes/Lidarr.Plugin.Common

# 2. B11 filename/path contract tests (locks format)
gh pr merge 289 --squash --repo RicherTunes/Lidarr.Plugin.Common

# 3. Ecosystem roadmap docs (no code)
gh pr merge 288 --squash --repo RicherTunes/Lidarr.Plugin.Common
```

### Phase 2: Tag Common Release

**Wait** until #290 and #289 are merged, then verify the release workflow will attach the canonical Abstractions DLL before tagging.

```bash
cd lidarr.plugin.common
git checkout main && git pull
# Verify src/Abstractions builds and ILRepack produces the expected DLL
dotnet build src/Abstractions -c Release
# Then tag
git tag v1.5.1 && git push origin v1.5.1
```

### Phase 3: Bump Submodules in Plugins

**Critical**: AppleMusicarr calls `-ResolveEntryPoints`, so its submodule **must** point to a commit that includes #290.

```bash
# Tidalarr
cd tidalarr
git checkout feat/tidalarr-ws3-cleanup
cd ext/Lidarr.Plugin.Common && git fetch origin && git checkout v1.5.1 && cd ../..
git add ext/Lidarr.Plugin.Common && git commit -m "chore: bump Common to v1.5.1"
git push

# Qobuzarr
cd qobuzarr
git checkout feat/canonical-abstractions
cd ext/Lidarr.Plugin.Common && git fetch origin && git checkout v1.5.1 && cd ../..
git add ext/Lidarr.Plugin.Common && git commit -m "chore: bump Common to v1.5.1"
git push

# Brainarr
cd brainarr
git checkout feat/canonical-abstractions
cd ext/lidarr.plugin.common && git fetch origin && git checkout v1.5.1 && cd ../..
git add ext/lidarr.plugin.common && git commit -m "chore: bump Common to v1.5.1"
git push

# AppleMusicarr (MUST be after #290 merge)
cd applemusicarr
git checkout feat/canonical-abstractions
cd ext/lidarr.plugin.common && git fetch origin && git checkout v1.5.1 && cd ../..
git add ext/lidarr.plugin.common && git commit -m "chore: bump Common to v1.5.1"
git push
```

### Phase 4: Merge Plugin PRs

```bash
# Tidalarr (WS3.1 cleanup first, then canonical abstractions)
gh pr merge 134 --squash --repo RicherTunes/Tidalarr
gh pr merge 133 --squash --repo RicherTunes/Tidalarr

# Qobuzarr
gh pr merge 157 --squash --repo RicherTunes/Qobuzarr

# Brainarr
gh pr merge 377 --squash --repo RicherTunes/Brainarr

# AppleMusicarr
gh pr merge 12 --squash --repo RicherTunes/AppleMusicarr
```

### Separate Lanes (Do Not Block Merge Train)

| Lane | Status | Notes |
|------|--------|-------|
| **CROSS_REPO_PAT** | Separate | Required for multi-plugin smoke only; doesn't block packaging PRs |
| **Version drift** | Separate | Brainarr (1.3.2 vs 1.3.1), AppleMusicarr (0.3.0-beta.1 vs beta.2) - fix in dedicated PRs to avoid conflicts |

### Local Verification (When CI Blocked)

If billing blocks CI, run packaging-closure locally using Docker:

```bash
# Per-plugin verification
cd <plugin-repo>
./scripts/packaging-closure.ps1  # or equivalent

# Or use the Common tools directly
pwsh -Command "& ./ext/Lidarr.Plugin.Common/tools/ManifestCheck.ps1 \
  -ProjectPath ./src/<Plugin>/<Plugin>.csproj \
  -ManifestPath ./plugin.json"
```

---

## Definition of Done

Full ecosystem parity is achieved when:

- [ ] All plugins ship the required type-identity assemblies and never ship forbidden host assemblies (policy tests + packaging-closure gates)
- [ ] Streaming plugins converge on a single filename/path contract (Common-first; no duplicate sanitizers)
- [ ] Persistent single-plugin E2E gates pass for Qobuzarr and Tidalarr (Schema→Configure→Search→AlbumSearch→Grab→Persist→Revalidation)
- [ ] Multi-plugin smoke test passes (Schema gate at minimum; full chain when host supports) and produces a valid run manifest
- [ ] Manifest entrypoint references are validated against built assemblies where applicable (no “type doesn’t exist” packaging debt)

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
**Status**: In PR (Common PR #289)

- [x] Multi-disc: D01Txx/D02Txx format validation
- [x] Extension normalization: `.flac` and `flac` both produce `.flac`
- [x] Unicode normalization: NFC form consistency

### 1.3 Common SHA Verification
- [x] Tidalarr: Uses ext-common-sha.txt
- [x] Qobuzarr: Uses ext-common-sha.txt
- [x] Brainarr: Uses ext-common-sha.txt (fixed format)

---

## Phase 2: Reduce Code Drift (Tech Debt)

### 2.1 Tidalarr Sanitization Consolidation
**Status**: In PR (Tidalarr PR #134)

Current duplicate paths:
- `TidalDownloadClient.cs:99-100` - FileNameSanitizer for title/artist
- `TidalDownloadClient.cs:405` - FileNameSanitizer for temp file path

Done (in PR):
- `TidalLidarrDownloadClient` routes artist/album path segments through `FileSystemUtilities.SanitizeFileName()` with a tripwire test.

Remaining:
- Route `TidalDownloadClient` through Common `FileSystemUtilities` anywhere it affects the on-disk path contract.

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

**E2E platform status**: Merged and in use (JSON run manifest schema + explicit-at-source error codes + golden fixtures).

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

## Phase 4: E2E Hardening (Future)

This phase is about reducing flakes and improving signal, not adding new gates.

### 4.1 Multi-Plugin Readiness

- [ ] Ensure multi-plugin smoke test runs in all plugin repos (requires `CROSS_REPO_PAT` in each caller repo)
- [ ] Keep multi-plugin results informational until the upstream Lidarr ALC fix is shipped in a published image

### 4.2 Reduce Flake Sources

- [ ] Replace any remaining `Task.Delay`/sleep-based assertions in tests with `FakeTimeProvider`
- [ ] Prefer deterministic selection (stable sort + intrinsic hash) anywhere we pick a “first” item in E2E

### 4.3 Manifest Tooling Adoption

- [ ] Wire `tools/ManifestCheck.ps1 -ResolveEntryPoints` into plugin `packaging-closure` where the plugin ships entryPoints (AppleMusicarr)
- [ ] Keep ManifestCheck usage consistent across plugin repos to prevent drift

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
- Track upstream: [Lidarr/Lidarr#5662](https://github.com/Lidarr/Lidarr/pull/5662) (ALC lifecycle fix)

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
- `tidalarr/src/Tidalarr/Integration/TidalDownloadClient.cs` (remaining consolidation work)

### E2E Scripts
- `lidarr.plugin.common/scripts/e2e-runner.ps1` - Main gate runner
- `lidarr.plugin.common/scripts/lib/e2e-gates.psm1` - Gate implementations
- `lidarr.plugin.common/scripts/lib/e2e-diagnostics.psm1` - Diagnostics bundle

---

## Changelog

| Date | Change |
|------|--------|
| 2025-12-31 | Common PR #187: JSON Schema + $schema fetchable pinning + job summary |
| 2026-01-17 | Canonical Abstractions release assets (no NuGet key required) |
| 2026-01-17 | ManifestCheck entrypoint resolution (`-ResolveEntryPoints`) + tests |
| 2026-01-17 | Filename/path contract tests expanded (multi-disc + Unicode + reserved names) |
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

---

## Extended Backlog (Parallelizable Work)

This section is intentionally designed to keep multiple agents busy without stepping on each other.

**Anti-bloat rule**: any new API or utility added to Common must delete measurable duplication in at least one plugin within 1–2 follow-up PRs.

### Lane A: Packaging + Manifest Guardrails (No Secrets)

- [ ] **Common**: Land `tools/ManifestCheck.ps1 -ResolveEntryPoints` (branch `feat/manifestcheck-resolve-entrypoints`, commit `24250f6`) and bump submodules in all plugins.
- [ ] **AppleMusicarr**: Make `manifest.json` reflect the net8 build reality (remove net6-only entrypoint, or compile the type for net8) and keep `packaging-closure` running `-ResolveEntryPoints`.
- [ ] **All plugins**: Standardize `packaging-closure` steps so each repo runs the same checks (canonical Abstractions hash, forbidden host assemblies, ManifestCheck).
- [ ] **Common**: Extend parity-lint rules to flag local `PreviewDetectionUtility` clones (and any future “magic bytes” validators) with a required deletion target.

### Lane B: Filename/Path Contract (No Secrets)

- [ ] **Common**: Land filename/path contract tests (PR #289) and bump submodules in all plugins.
- [ ] **Tidalarr**: Finish consolidation in `TidalDownloadClient.cs` so all on-disk paths are routed through `FileSystemUtilities` (remove remaining `FileNameSanitizer` usage).
- [ ] **Common**: Add small “contract fixture” helper so plugins can assert path outputs without duplicating formatting logic.

### Lane C: Secret Protection Convergence (Low Secrets)

- [ ] **Common**: Keep the secret protection facade API stable (docs + tests) and explicitly deprecate plugin-local crypto patterns.
- [ ] **AppleMusicarr**: Complete the migration away from plugin-local crypto and delete duplicate implementations (target: remove `DataProtector.cs` and any embedded keyfile logic).
- [ ] **Qobuzarr/Tidalarr**: Audit for duplicate secure credential managers / sanitizers; upstream primitives only when they delete plugin-local copies.

### Lane D: Hosting Convergence (Medium Risk)

- [ ] **Tidalarr**: Complete the StreamingPlugin migration without breaking the CLI diagnostics contract (delete host-wiring duplication only once characterization tests pass).
- [ ] **Qobuzarr**: Avoid “stub adapters”. Only add a modern host entrypoint if it delegates to real legacy functionality (no silent no-ops).

### Lane E: Resilience Parity (No Secrets)

- [ ] **Brainarr**: Verify WS4.2 adapter migration stays behavior-compatible via characterization tests, then delete any remaining legacy resilience helpers.
- [ ] **Common**: Maintain AdvancedCircuitBreaker as a pure primitive; avoid provider-specific policy.

### Lane F: CI + Multi-Plugin Smoke (Requires Secrets)

- [ ] **Repo admin**: Add `CROSS_REPO_PAT` secret to Qobuzarr/Tidalarr/Brainarr and re-run multi-plugin smoke tests.
- [ ] **Common**: Improve multi-plugin smoke workflow messages for fork PRs (skip vs fail) and missing secrets (fail-fast, actionable).

### Lane G: “Weeks of Work” Queue (Nice-to-Fix)

- [ ] **All repos**: Reduce flaky tests by replacing time/sleep with `FakeTimeProvider` and making parallel builds deterministic (`--disable-build-servers`, `-m:1` where needed).
- [ ] **Common**: Expand `E2E_ERROR_CODES.md` contract tables with example `details` payloads and keep the tripwire tests green.
- [ ] **Common**: Add a one-command “ecosystem verification” script that runs local packaging-closure + minimal E2E schema gate for all plugins (no credentials).
