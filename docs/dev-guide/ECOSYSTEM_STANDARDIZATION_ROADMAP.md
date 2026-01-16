# Ecosystem Standardization Roadmap (Thin Common)

This roadmap is for cross-plugin parity work where it makes sense to standardize behavior and delete duplication.

Guiding rule (anti-bloat):
- Any new `lidarr.plugin.common` API must enable deleting a concrete duplicate in at least one plugin repo within 1–2 follow-up PRs.

Non-goals:
- Do not move provider-specific logic (Qobuz signing/auth extraction, Tidal manifest/DRM, Brainarr prompt policy, AppleMusic SDK specifics) into Common.
- Do not force one manifest schema where host contracts do not exist (Abstractions currently has no ImportList contract).

## Current Baseline (Already Done)
- Multi-plugin E2E harness + JSON manifest + golden fixtures + explicit-at-source error codes.
- Packaging preflight + Abstractions SHA mismatch detection.
- Shared filename/path helpers in `FileSystemUtilities` and shared download payload validation in `DownloadPayloadValidator`.

## Current Status Snapshot (2026-01-15)
This section is the “what do we do next?” view; the milestone checklists below remain the long-term structure.

**Stable + proven (ship behavior)**
- 3-plugin Docker bootstrap: Qobuzarr + Tidalarr (full gates) + Brainarr (schema/importlist gates, opt-in LLM).
- E2E runner is safe-by-default: explicit error codes, redaction, deterministic selection, JSON schema validation, and contract tripwires.
- CI hygiene: change-detection self-test, path normalization tests, docs-only builds still report status.

**In-flight (work on this branch / pending PRs)**
- W1 AppleMusicarr: manifest/entryPoint mismatch confirmed (net6-only ImportList entryPoint referenced) and crypto duplication confirmed; implementation work pending.
- M2 Common token protection facade: already available via `IStringProtector` / `StringTokenProtector` (versioned prefix `lpc:ps:v1:`). Next step is downstream adoption: delete AppleMusicarr custom crypto within ≤2 follow-up PRs.
- M2 publishing: `release.yml` supports nuget.org publish, but requires `NUGET_API_KEY` secret (optional; prefer “thin common” even if using GitHub Packages).   

**Known upstream dependency**
- Lidarr multi-plugin ALC lifecycle fix: tracking `Lidarr/Lidarr#5662` for a published Docker tag that contains the fix.

## Active Workstreams (Multi-Agent Safe)
| Workstream | Repo(s) | Goal (deletion-driven) | Status | Hot files (avoid overlap) |
|------------|---------|------------------------|--------|----------------------------|
| WS1 | AppleMusicarr + Common | Replace custom crypto with `IStringProtector` and delete duplicate protectors | Open | `applemusicarr/src/AppleMusicarr.Plugin/Security/**`, `applemusicarr/src/AppleMusicarr.Plugin/Stores/**` |
| WS2 | Qobuzarr | Delete local `PreviewDetectionUtility` clone and route all callers to Common | Open | `qobuzarr/src/Utilities/**`, `qobuzarr/src/**/Preview*` |
| WS3 | Tidalarr | Refactor `TidalarrPlugin` to inherit `StreamingPlugin<Module,Settings>` and delete duplicated host wiring | Open | `tidalarr/src/Tidalarr/Integration/TidalarrPlugin.cs`, `tidalarr/src/Tidalarr/Integration/TidalModule.cs` |
| WS4 | Brainarr | Consolidate circuit breaker/resilience (pick one authority, delete the other) | Open | `brainarr/Brainarr.Plugin/Resilience/**`, `brainarr/Brainarr.Plugin/Services/Resilience/**` |
| WS5 | Common tooling | Parity-lint expansion to include AppleMusicarr + low-noise rules | Open | `scripts/parity-lint.ps1`, `scripts/tests/Test-ParityLint.ps1` |
| WS6 | Ecosystem build | Publish Abstractions/Common via NuGet and migrate plugins from ProjectReference to PackageReference | Blocked on secrets | `.github/workflows/release.yml`, plugin `Directory.Packages.props` / `NuGet.config` |

## Milestones (PR-Sized)

### M1 — Remove obvious clones (quick wins)
**Target:** Delete plugin-local copies of utilities that already exist in Common.

- [x] Qobuzarr: delete preview/sample detection clone(s) and use `Lidarr.Plugin.Common.Utilities.PreviewDetectionUtility` everywhere.
  - Enforcement: `qobuzarr/tests/Qobuzarr.Tests/Unit/Utilities/PreviewDetectionUtilityCloneTests.cs` prevents reintroduction.
  - Acceptance: `dotnet test qobuzarr/Qobuzarr.sln -c Release` passes.
- [ ] AppleMusicarr: prove manifest entryPoints resolve for the TFM(s) they ship (net8 `plugin.json`, optional net6 `manifest.json`) and delete any dead entryPoints/types.
  - Acceptance: `dotnet test applemusicarr/AppleMusicarr.sln -c Release` passes; entryPoint-resolution tests prevent reintroducing “type does not exist” packaging debt.

### M2 — Public token protection facade in Common (enables deletion)
**Target:** Stop downstream plugins from implementing their own encryption primitives.

- [x] Common: expose a small, stable public API for protecting strings at rest.
  - Delivered: `IStringProtector` + `StringTokenProtector` (versioned prefix `lpc:ps:v1:`) registered by `AddTokenProtection()`.
  - Acceptance: Common tests pass; no breaking changes to existing consumers.
- [ ] AppleMusicarr follow-up: replace `AppleMusicarr.Plugin.Security.DataProtector` with the Common facade.
  - Acceptance: existing encrypted values can be read (dual-read/migration), and new writes use the Common format; E2E secrets redaction still holds.

### M3 — Manifest/entrypoint reality checks (correctness ROI)
**Target:** Catch “manifest points at a type that does not exist in net8 build” problems early.

- [x] Common: `tools/ManifestCheck.ps1` supports `-ValidateEntryPoints` for `plugin.json` `entryPoints` (metadata-only; no `Assembly.Load`).
  - Acceptance: `tests/ManifestCheckScriptTests.cs` covers success + `ENT001` missing-type behavior.
- [x] Common: `tools/ManifestCheck.ps1 -ValidateEntryPoints` supports `manifest.json` `entryPoints` (AppleMusicarr-style `entryPoints: [{ type, implementation }]`) (metadata-only; no `Assembly.Load`).
  - Acceptance: `tests/ManifestCheckScriptTests.cs` covers `plugin.json` and `manifest.json` success + `ENT001` missing-type behavior.
- [ ] AppleMusicarr follow-up: align `manifest.json` / `plugin.json` with net8 build outputs; fix any stale docs mentioning net6-only types.

### M4 — Standardize hosting entrypoint pattern where possible (reduce drift)
**Target:** converge on `StreamingPlugin<Module, Settings>` for net8 plugin hosts while keeping legacy adapters where required.

- [ ] Tidalarr: refactor `TidalarrPlugin` to inherit from Common `StreamingPlugin` (module already derives from `StreamingPluginModule`).
  - Acceptance: schema + search + grab E2E passes; no settings regressions; legacy Lidarr-native wrappers remain intact.
- [ ] Qobuzarr: add a new `StreamingPlugin` entrypoint for settings/DI standardization without changing legacy `HttpIndexerBase`/`DownloadClientBase` behavior.
  - Acceptance: no runtime behavior change; packaging/E2E unchanged; settings definitions consistent.

### M5 — Brainarr resilience split-brain cleanup (delete duplication)
**Target:** pick one circuit breaker/resilience stack and remove the other.

- [ ] Brainarr: decide authoritative breaker (prefer Common `CircuitBreaker` + Brainarr-specific profiles) and delete the duplicate implementation.
  - Acceptance: provider call behavior unchanged (characterization tests), and test suite stays deterministic (no `Task.Delay`).

### M6 — Host-coupled dependency discipline (prevent regressions)
**Target:** ensure plugins do not compile against runtime-incompatible versions of host-boundary assemblies.

- [ ] AppleMusicarr: remove/adjust `Microsoft.Extensions.Logging.Abstractions` net8 reference drift (currently a candidate for runtime mismatch).
  - Acceptance: plugin loads in the pinned Lidarr host tag; no `MissingMethodException` / `TypeLoadException`.
- [ ] Common: document “derive versions from host assemblies” as the only source of truth and keep check-host-versions tooling up to date.

### M7 — Parity lint expansion (keep drift from returning)
**Target:** extend `scripts/parity-lint.ps1` to cover all plugin repos in this workspace.

- [ ] Add `applemusicarr/` to the parity-lint repo list.
- [ ] Add rules only after proving low false positives (always include an expiry-driven baseline entry when needed).

## Parallel Work (Non-overlapping)
- **AI A:** WS1 AppleMusicarr M2 follow-up (migrate `DataProtector` → `IStringProtector`, delete duplicate crypto).
- **AI B:** WS2 Qobuzarr M1 clone deletion (remove local `PreviewDetectionUtility`, add parity-lint rule).
- **AI C:** WS3 Tidalarr M4 hosting convergence (`TidalarrPlugin` → `StreamingPlugin`, delete duplicated host wiring).
- **AI D:** WS4 Brainarr M5 resilience consolidation (characterization tests first, then delete duplicate circuit breaker).
- **AI E:** WS5 parity-lint expansion (include AppleMusicarr, add low-noise rules with expiry baselines).

## Work Queue (6+ weeks, safe parallelism)
These are intentionally PR-sized and scoped to avoid merge conflicts. Each item lists “hot files” to prevent two agents editing the same core surfaces.

### W1 — Common token protection (finish + ship)
- Goal: treat `IStringProtector` as the canonical at-rest string protection facade and delete custom crypto in at least one downstream repo within 1–2 follow-up PRs.
- Hot files: `src/Security/TokenProtection/*`, `src/Extensions/ServiceCollectionExtensions.cs`, `docs/dev-guide/TOKEN_PROTECTION.md`.
- Acceptance: `dotnet test -c Release --no-build` passes; a downstream repo (AppleMusicarr) removes its custom protector.

### W2 — AppleMusicarr correctness (manifest reality + crypto migration)        
- Goal: ensure shipped entryPoints resolve for the TFM(s) AppleMusicarr builds and converge encryption to `IStringProtector` (with migration + deletion).
- Hot files: `applemusicarr/src/AppleMusicarr.Plugin/manifest.json`, `applemusicarr/src/AppleMusicarr.Plugin/Security/DataProtector.cs`.
- Acceptance: `dotnet build applemusicarr/src/AppleMusicarr.Plugin/AppleMusicarr.Plugin.csproj -c Release` passes; manifests match compiled types; old encrypted values migrate.

### W3 — Hosting convergence (reduce drift without breaking legacy)
- Goal: converge on `StreamingPlugin<Module, Settings>` where already structurally aligned.
- Hot files: `tidalarr/src/Tidalarr/Integration/TidalarrPlugin.cs`, new Qobuzarr entrypoint file(s) under `qobuzarr/src/Integration/`.
- Acceptance: E2E bootstrap passes unchanged; legacy wrappers remain thin.

### W4 — Brainarr resilience split-brain cleanup
- Goal: pick one breaker/policy path, delete the other, keep behavior stable via characterization tests.
- Hot files: `brainarr/Brainarr.Plugin/Resilience/*`, `brainarr/Brainarr.Plugin/Services/Resilience/*`.
- Acceptance: provider-failure behavior unchanged; no `Task.Delay` in tests; circuit breaker tests use `FakeTimeProvider`.

### W5 — Parity-lint expansion + “delete duplicates” automation
- Goal: expand parity-lint to include `applemusicarr/`, add low-false-positive rules, enforce expiry+owner+issue URL.
- Hot files: `scripts/parity-lint.ps1`, `scripts/tests/Test-ParityLint.ps1`.
- Acceptance: parity-lint is quiet on `main` with justified baselines; violations are actionable and not noisy.

### W6 — E2E gates raising (optional, credentialed)
- Goal: keep raising confidence without slowing default runs.
- Ideas: post-import verification (opt-in), richer metadata assertions (opt-in), provider canary job (continue-on-error).
- Hot files: `scripts/e2e-runner.ps1`, `scripts/lib/e2e-gates.psm1`, `.github/workflows/e2e-bootstrap.yml`.
- Acceptance: opt-in gates improve signal without breaking local dev; manifests remain schema-valid.

## Coordination (Multi-Agent Safety)
To keep multiple agents productive without stepping on each other:

- **Claim work**: Add your name/handle next to an item (W1–W6/M1–M7) and push a PR-sized branch.
- **One hot surface per PR**: Don’t mix edits to `scripts/e2e-runner.ps1` with `scripts/lib/e2e-gates.psm1` unless the PR is explicitly E2E-focused.
- **Deletion-first rule**: If you add a Common API, your next PR (or the next agent’s PR) must delete the corresponding duplication in a plugin repo within 1–2 PRs.
- **Avoid overlap**: Before editing a “hot files” set, check if another open PR is touching those exact files.

## Definition of Done (Pragmatic “100% parity”)
- No duplicated generic utilities across plugin repos (preview detection, filename sanitization, payload validation, common retry/breaker primitives).
- Token protection uses a Common public facade; no plugin ships custom crypto primitives.
- Hosting pattern is consistent for net8 plugin hosts: module + `StreamingPlugin` (legacy adapters allowed but thin).
- E2E bootstrap can configure and validate Qobuzarr + Tidalarr + Brainarr (schema/importlist) without UI steps (using env vars/secrets).
- Parity lint stays quiet on `main`, and any baseline exception has an owner + expiry + issue link.
