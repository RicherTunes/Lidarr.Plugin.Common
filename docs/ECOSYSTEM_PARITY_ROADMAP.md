# Ecosystem Parity Roadmap

This document tracks progress toward full structural and behavioral parity across the plugin ecosystem (Tidalarr, Qobuzarr, Brainarr, AppleMusicarr).

## Current Status

| Dimension | Tidalarr | Qobuzarr | Brainarr | AppleMusicarr | Common |
|-----------|----------|----------|----------|---------------|--------|
| **Packaging** | ✅ | ✅ | ✅ | ⚠️ (manifest/entrypoints drift) | Policy complete |
| **Naming/Path** | ✅ | ✅ | N/A | N/A | FileSystemUtilities |
| **Concurrency** | ✅ | ✅ | N/A | N/A | Download orchestrators |
| **Auth Lifecycle** | ✅ (single authority) | ✅ (single authority) | N/A | N/A | Auth patterns |
| **E2E Gates** | ✅ Proven | ✅ Proven | ✅ Schema+ImportList | ⚠️ (not yet in harness) | Manifest + gates + fixtures |

**Streaming parity (Tidalarr/Qobuzarr): ~99%**  
**Ecosystem parity (incl. Brainarr/AppleMusicarr): ~90–95%**

---

## Workstreams (Multi-Agent, Weeks-Scale Backlog)

Keep parity work deletion-driven:
- Any new `lidarr.plugin.common` helper must enable deleting real duplication in a plugin repo within 1–2 follow-up PRs.
- Prefer characterization tests + deletion over “new shared utilities”.

| Workstream | Goal | Status | Hot Files (avoid overlap) |
|-----------|------|--------|----------------------------|
| **WS1: AppleMusicarr correctness** | Align manifests/entrypoints with net8 builds; remove dead/legacy entrypoints; bring AppleMusicarr into the same packaging/validation contract | Open | `applemusicarr/src/**/manifest.json`, `applemusicarr/src/**/plugin.json`, `tools/ManifestCheck.ps1`, `tests/ManifestCheck*` |
| **WS2: Secret protection convergence** | Add a stable, versioned protected-string facade in Common, then delete plugin-local crypto (AppleMusicarr) within 1–2 PRs | Open | `src/Security/TokenProtection/**`, `docs/dev-guide/TOKEN_PROTECTION.md`, `applemusicarr/src/**/Security/*` |
| **WS3: Hosting convergence** | Reduce drift by standardizing plugin host wiring where it already matches `StreamingPlugin<Module, Settings>` patterns | Open | `tidalarr/src/Tidalarr/Integration/TidalarrPlugin.cs`, `qobuzarr/src/**/Settings*`, `lidarr.plugin.common/src/Hosting/**` |
| **WS4: Brainarr resilience migration** | Migrate Brainarr’s active circuit breaker to Common (preserving semantics), then delete the plugin-local breaker implementation | Open | `brainarr/Brainarr.Plugin/Services/Resilience/**`, `lidarr.plugin.common/src/Services/Resilience/**` |
| **WS5: Parity lint expansion** | Prevent drift by scanning all plugin repos and flagging re-invented primitives with low false positives | In review | `scripts/parity-lint.ps1`, `scripts/tests/Test-ParityLint.ps1` |
| **WS7: CI parity** | Make multi-plugin smoke tests fail fast with actionable guidance when required secrets/permissions are missing | In review | `.github/workflows/multi-plugin-smoke-test.yml`, `.github/actions/**` |
| **WS6: Abstractions distribution** | Publish Abstractions/Common as packages and migrate plugins away from `ProjectReference` where it causes ABI/MVID drift | Blocked on secrets/config | `.github/workflows/release.yml`, plugin `NuGet.config`, `Directory.Packages.props` |

### WS4 Prerequisites (Do Not “Delete First”)

Brainarr’s circuit breaker is **active** (protects AI provider invocations keyed by provider:model). WS4 is a migration and requires Common to support Brainarr’s semantics before any deletion:
- Failure-rate + minimum-throughput windowing (Brainarr uses “last N operations”, not just failure timestamps).
- Optional cancellation-as-failure (Brainarr often sees timeouts as `TaskCanceledException`; Common currently treats cancellation differently).
- Characterization tests must prove equivalence for the scenarios Brainarr already relies on before switching the implementation.

Suggested parallelism (non-overlapping):
- Agent A: WS1 (AppleMusicarr manifest/entrypoints) + tests.
- Agent B: WS2 (Common protected-string facade) + tests; Agent C follows by deleting AppleMusicarr crypto.
- Agent D: WS3 (Tidalarr hosting) + E2E bootstrap; Agent E: WS4 Phase A (Common breaker enhancements) + tests.
- Agent F: WS4 Phase B (Brainarr migration) once WS4 Phase A merges.
- Agent G: WS6 packaging/distribution plan + one-plugin migration pilot.

---

## Milestones (PR-Sized, Deletion-Driven)

Each Common addition should delete measurable duplication within 1–2 follow-up PRs.

| Milestone | Scope | Goal | Delete Target | Acceptance |
|----------|-------|------|---------------|------------|
| **M1** | Qobuzarr | Delete duplicated `PreviewDetectionUtility` and use Common everywhere | Delete `qobuzarr/src/Utilities/PreviewDetectionUtility.cs` | `dotnet build qobuzarr/Qobuzarr.sln -c Release` |
| **M2** | Common → AppleMusicarr | Add stable protected-string facade + migrate AppleMusicarr off custom crypto | Delete AppleMusicarr `DataProtector` and file-store `SecretProtector` copy | Common + AppleMusicarr tests green; settings decrypt after restart |
| **M3** | AppleMusicarr | Fix manifest/entrypoint reality (net8) + add entrypoint resolution test | Remove/repair invalid entryPoints in `manifest.json` | Packaging test fails if entrypoint type missing |
| **M4** | Tidalarr | Move host wiring to `StreamingPlugin<Module, Settings>` where possible | Delete manual manifest/settings host plumbing in `TidalarrPlugin` | `dotnet test tidalarr/Tidalarr.sln -c Release` + E2E bootstrap |
| **M5** | Qobuzarr | Add modern host entrypoint (no behavior change) for settings/DI parity | Avoid introducing parallel host bootstrap patterns | Plugin loads; legacy behavior unchanged |
| **M6** | Brainarr | Migrate active circuit breaker to Common (preserve semantics), then delete plugin-local breaker | Delete `brainarr/Brainarr.Plugin/Services/Resilience/CircuitBreaker.cs` | Brainarr tests green; characterization tests still pass |
| **M7** | Ecosystem | Manifest/schema coherence and tooling | Remove legacy manifest formats where safe; document purpose | All plugin manifests validated by Common tooling |

### Common-Only Improvements (Must Unlock Deletions)

These are “good ideas” only if they enable deletion of plugin-local copies in ≤2 follow-up PRs.

| Item | Goal | Required Deletion Follow-Up |
|------|------|-----------------------------|
| Safe HTTP logging | Make `BuildForLogging()`/equivalent redact query params by default | Delete any plugin-local URL/query redaction used only for logging |
| Reusable token store factory | Make `FileTokenStore`/equivalent storage reusable without copy-paste | Delete any plugin-local token store/locking/atomic-write implementations |
| Sanitization primitives | Add small, reusable primitives (control/zero-width stripping, whitespace collapse, query redaction) | Delete duplicated sanitizer primitives (keep provider policy local) |

---

## Definition of Done

Full ecosystem parity is achieved when:

- [ ] All plugin packages follow the minimal packaging contract (plugin assembly + `Lidarr.Plugin.Abstractions.dll` + `plugin.json`; no forbidden host-provided DLLs)
- [ ] Both streaming plugins produce identical filename format on multi-disc and edge sanitization
- [ ] Persistent single-plugin E2E gates pass for Qobuzarr and Tidalarr
- [ ] Multi-plugin bootstrap passes for Qobuzarr+Tidalarr+Brainarr (host permitting); AppleMusicarr is at least Schema-valid where applicable

**No-Drift Rule**: Any new filename/path logic must either live in Common or delegate to Common.

---

## Type-Identity Assembly Policy

### Required Package Contents (All Plugins)
Plugins **MUST** ship:
- The plugin assembly (typically merged output, e.g. `Lidarr.Plugin.<Name>.dll`)
- `Lidarr.Plugin.Abstractions.dll`
- `plugin.json`

Plugins may ship additional assemblies **only** via an explicit allow/keep list (plugin-specific), and should prefer merge/internalize over shipping loose dependencies.

### Forbidden Assemblies (All Plugins)
Plugins **MUST NOT** ship these assemblies:
- `FluentValidation.dll` - Host provides; shipping causes type-identity conflicts (override signatures / `ValidationFailure` identity)
- `Microsoft.Extensions.DependencyInjection.Abstractions.dll` - Host provides; shipping breaks DI contracts
- `Microsoft.Extensions.Logging.Abstractions.dll` - Host provides; shipping breaks ILogger contracts
- `System.Text.Json.dll` - Cross-boundary type identity risk
- `NLog.dll` - Host provides; shipping causes logging/type identity conflicts
- `Lidarr.Core.dll`, `Lidarr.Common.dll`, `Lidarr.Host.dll` - Host assemblies   
- `NzbDrone.*.dll` - Legacy host assemblies

### ⚠️ Policy Warning: Changes Must Be Deliberate

This policy exists to prevent **type identity** failures across the host/plugin boundary and across multiple plugins loaded together.
If you need a new “allowed/keep” DLL, add it deliberately and pair it with:
1. A deletion plan (remove duplication elsewhere), and
2. A packaging preflight test covering the new contract.

---

## Phase 1: Lock Contracts with Tests (High Priority)

### 1.1 Packaging Content Tests
- [x] **Common**: Package preflight validator (required + forbidden DLLs)
- [x] **Tidalarr**: Packaging policy baseline updated to the minimal contract
- [x] **Qobuzarr**: Packaging policy tests aligned to the minimal contract
- [x] **Brainarr**: Packaging policy tests aligned to the minimal contract

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

**Runner capabilities (landed)**:
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

## Changelog

| Date | Change |
|------|--------|
| 2025-12-31 | E2E manifest schema + `$schema` pinning + job summary (landed) |
| 2025-12-31 | Qobuzarr PR4: Dead code deletion + 8 auth characterization tests (incl. DI same-instance) |
| 2025-12-31 | Tidalarr PR3: TidalOAuthService fallback → FailOnIOTokenStore (no silent temp writes) |
| 2025-12-31 | Tidalarr PR2: Auth lifecycle unification - single token authority, scoped IStreamingTokenProvider |
| 2025-12-31 | E2E bootstrap validation: All gates pass for Tidalarr (Schema→Persist→Revalidation) |
| 2025-12-31 | Added Auth Lifecycle Hardening section documenting single-authority pattern |
| 2025-12-30 | Added multi-plugin stability caveat (:8691 best-effort until Lidarr ALC fix) |
| 2025-12-30 | PR #186: SimpleDownloadOrchestrator metadata tagging + ILogger + fail-fast |
| 2025-12-27 | Brainarr PR #346: FluentValidation exclusion fix with guard test |
| 2025-12-27 | Added type-identity packaging policy section (forbidden host-provided DLLs) |
| 2025-12-27 | E2E gates: credential skip semantics, indexer/test preference, URL redaction |
| 2025-12-27 | 3-plugin coexistence proven: Qobuzarr, Tidalarr, Brainarr all pass Schema gate |
| 2025-12-27 | Tidalarr PR #104: sanitization consolidation with 4 unit tests |
| 2025-12-27 | Tidalarr PR #105: chunk delay configurability with clamping (0-2000ms) |
| 2025-12-27 | Common PRs #167/#168 merged - packaging policy complete |
| 2025-12-27 | Packaging baseline updated to minimal contract |
| 2025-12-27 | Qobuzarr TrackFileNameBuilder delegated to Common |
| 2025-12-27 | Initial roadmap created |
