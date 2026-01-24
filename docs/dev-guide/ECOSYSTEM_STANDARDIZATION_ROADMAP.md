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

## Current Status Snapshot (2026-01-16)
This section is the “what do we do next?” view; the milestone checklists below remain the long-term structure.

**Stable + proven (ship behavior)**
- 3-plugin Docker bootstrap: Qobuzarr + Tidalarr (full gates) + Brainarr (schema/importlist gates, opt-in LLM).
- E2E runner is safe-by-default: explicit error codes, redaction, deterministic selection, JSON schema validation, and contract tripwires.
- CI hygiene: change-detection self-test, path normalization tests, docs-only builds still report status.

**Recently completed (merged / green in CI)**
- WS1 AppleMusicarr: legacy `manifest.json` / net6 import-list scaffolding removed (net8-only); `plugin.json` is the single source of truth.
- WS1 AppleMusicarr: custom at-rest crypto deleted in favor of Common `IStringProtector` (legacy `enc:v1:` is read-only for migration when the old `.key` exists).
- WS2 Qobuzarr: local preview/sample detection clone deleted; `PreviewDetectionUtilityCloneTests` prevents reintroduction.
- M3: `tools/ManifestCheck.ps1 -ValidateEntryPoints` catches entrypoint/type mismatches early.

**In-flight (next high-ROI work)**
- WS3 Tidalarr: refactor `TidalarrPlugin` to inherit `StreamingPlugin<Module, Settings>` and delete duplicated host wiring (M4).
- WS4 Brainarr: consolidate circuit breaker/resilience (“one authority”) and delete the duplicate implementation (M5).
- WS5 parity-lint: include AppleMusicarr and add low-noise rules that directly delete drift.
- WS7 CI parity: ensure multi-plugin smoke test wrappers + required secrets (e.g., `CROSS_REPO_PAT`) fail fast with actionable errors; fix pre-existing CI extraction issues where needed.
- WS6 ecosystem build (optional): publish Abstractions/Common via NuGet and migrate plugins from ProjectReference to PackageReference (blocked on `NUGET_API_KEY` secret if using nuget.org).

**Known upstream dependency**
- Lidarr multi-plugin ALC lifecycle fix: tracking `Lidarr/Lidarr#5662` for a published Docker tag that contains the fix.

## Active Workstreams (Multi-Agent Safe)
| Workstream | Repo(s) | Goal (deletion-driven) | Status | Hot files (avoid overlap) |
|------------|---------|------------------------|--------|----------------------------|
| WS1 | AppleMusicarr + Common | Replace custom crypto with `IStringProtector` and delete duplicate protectors | Done | `applemusicarr/src/AppleMusicarr.Plugin/Security/**`, `applemusicarr/src/AppleMusicarr.Plugin/Stores/**` |
| WS2 | Qobuzarr | Delete local `PreviewDetectionUtility` clone and route all callers to Common | Done | `qobuzarr/src/Utilities/**`, `qobuzarr/src/**/Preview*` |
| WS3 | Tidalarr | Refactor `TidalarrPlugin` to inherit `StreamingPlugin<Module,Settings>` and delete duplicated host wiring | Open | `tidalarr/src/Tidalarr/Integration/TidalarrPlugin.cs`, `tidalarr/src/Tidalarr/Integration/TidalModule.cs` |
| WS4 | Brainarr | Consolidate circuit breaker/resilience (pick one authority, delete the other) | Open | `brainarr/Brainarr.Plugin/Resilience/**`, `brainarr/Brainarr.Plugin/Services/Resilience/**` |
| WS5 | Common tooling | Parity-lint expansion to include AppleMusicarr + low-noise rules | Open | `scripts/parity-lint.ps1`, `scripts/tests/Test-ParityLint.ps1` |
| WS6 | Ecosystem build | Publish Abstractions/Common via NuGet and migrate plugins from ProjectReference to PackageReference | Blocked on secrets | `.github/workflows/release.yml`, plugin `Directory.Packages.props` / `NuGet.config` |
| WS7 | CI parity | Unify reusable multi-plugin smoke tests + required secrets/permissions across all plugin repos | Open | `.github/workflows/*`, repo settings/secrets |

## Task Board (PR-sized; claim by editing this file)
Keep items deletion-driven. Each “Common addition” must have a follow-up PR that deletes downstream duplication within 1–2 PRs.

- [ ] **P0 / WS3.1 (Tidalarr)** Refactor `TidalarrPlugin` to inherit `StreamingPlugin<Module, Settings>`; delete duplicated host wiring; keep `OAuthAuthUrl` UX.
  - Hot files: `tidalarr/src/Tidalarr/Integration/TidalarrPlugin.cs`, `tidalarr/src/Tidalarr/Integration/TidalModule.cs`
  - Acceptance: Tidalarr unit tests pass; E2E bootstrap still passes with persisted auth.
- [ ] **P0 / WS7.1 (CI parity)** Multi-plugin smoke test: fail fast with a clear message when `CROSS_REPO_PAT` (or equivalent) is missing (no silent checkout failures).
  - Hot files: `.github/workflows/multi-plugin-smoke-test.yml`, `.github/actions/init-common-submodule/*`
  - Acceptance: a repo without the secret fails with an actionable error code/message; a repo with the secret runs end-to-end.
- [ ] **P0 / WS5.1 (parity-lint)** Include `applemusicarr/` in parity-lint scan list; keep noise low with expiry+owner baselines.
  - Hot files: `scripts/parity-lint.ps1`, `scripts/tests/Test-ParityLint.ps1`
  - Acceptance: parity-lint stays quiet on `main`; violations are actionable (why + fix link).
- [ ] **P1 / WS4.1 (Brainarr)** Add characterization tests that lock current resilience behavior (timeouts, retry counts, circuit open/half-open) and pick one breaker implementation to keep.
  - Hot files: `brainarr/Brainarr.Plugin/Resilience/**`, `brainarr/Brainarr.Plugin/Services/Resilience/**`
  - Acceptance: deterministic tests (`FakeTimeProvider`), no `Task.Delay` sleeps.
- [ ] **P1 / WS4.2 (Brainarr)** Delete the non-authoritative circuit breaker implementation and rewire call sites to the chosen authority.
  - Hot files: same as WS4.1
  - Acceptance: behavior matches characterization tests; one breaker remains.
- [ ] **P1 / M6.1 (Host deps)** Normalize host-coupled dependency versions across plugins (FluentValidation/NLog/Microsoft.Extensions.*) to match host extraction tooling.
  - Acceptance: packaging preflight stays green; no `MissingMethodException` / `TypeLoadException` in multi-plugin bootstrap.
- [ ] **P2 / WS6.1 (NuGet)** (Optional) Publish Abstractions/Common to nuget.org (or formalize GitHub Packages usage) and migrate one plugin from ProjectReference → PackageReference to prove byte-identical Abstractions.
  - Acceptance: multi-plugin Abstractions SHA mismatch cannot occur for that plugin.

## Milestones (PR-Sized)

### M1 — Remove obvious clones (quick wins)
**Target:** Delete plugin-local copies of utilities that already exist in Common.

- [x] Qobuzarr: delete preview/sample detection clone(s) and use `Lidarr.Plugin.Common.Utilities.PreviewDetectionUtility` everywhere.
  - Enforcement: `qobuzarr/tests/Qobuzarr.Tests/Unit/Utilities/PreviewDetectionUtilityCloneTests.cs` prevents reintroduction.
  - Acceptance: `dotnet test qobuzarr/Qobuzarr.sln -c Release` passes.
- [x] AppleMusicarr: remove legacy `manifest.json` / net6-only entrypoint plumbing and treat `plugin.json` as the single source of truth for net8 packaging.
  - Acceptance: `dotnet test applemusicarr/AppleMusicarr.sln -c Release` passes; scripts validate `plugin.json` only.

### M2 — Public token protection facade in Common (enables deletion)
**Target:** Stop downstream plugins from implementing their own encryption primitives.

- [x] Common: expose a small, stable public API for protecting strings at rest.
  - Delivered: `IStringProtector` + `StringTokenProtector` (versioned prefix `lpc:ps:v1:`) registered by `AddTokenProtection()`.
  - Acceptance: Common tests pass; no breaking changes to existing consumers.
- [x] AppleMusicarr follow-up: replace `AppleMusicarr.Plugin.Security.DataProtector` with the Common facade.
  - Acceptance: legacy `enc:v1:` values are read-only (requires the legacy `.key` file); new writes are `lpc:ps:v1:`; tests pass.

### M3 — Manifest/entrypoint reality checks (correctness ROI)
**Target:** Catch “manifest points at a type that does not exist in net8 build” problems early.

- [x] Common: `tools/ManifestCheck.ps1` supports `-ValidateEntryPoints` for `plugin.json` `entryPoints` (metadata-only; no `Assembly.Load`).
  - Acceptance: `tests/ManifestCheckScriptTests.cs` covers success + `ENT001` missing-type behavior.
- [x] Common: `tools/ManifestCheck.ps1 -ValidateEntryPoints` supports `manifest.json` `entryPoints` (AppleMusicarr-style `entryPoints: [{ type, implementation }]`) (metadata-only; no `Assembly.Load`).
  - Acceptance: `tests/ManifestCheckScriptTests.cs` covers `plugin.json` and `manifest.json` success + `ENT001` missing-type behavior.
- [x] AppleMusicarr follow-up: remove legacy `manifest.json` and align all packaging/validation to `plugin.json`.

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
- **AI A:** WS3 Tidalarr M4 hosting convergence (`TidalarrPlugin` → `StreamingPlugin`, delete duplicated host wiring).
- **AI B:** WS4 Brainarr M5 resilience consolidation (characterization tests first, then delete duplicate circuit breaker).
- **AI C:** WS5 parity-lint expansion (include AppleMusicarr, add low-noise rules with expiry baselines).
- **AI D:** WS7 CI parity (standardize reusable workflow wrapper + required secrets like `CROSS_REPO_PAT`, and fix any pre-existing “missing host assemblies” CI gaps).
- **AI E:** M6 host-coupled dependency discipline (tighten dependency pins + packaging exclusions where needed; targeted, deletion-driven).
- **AI F:** WS6 ecosystem build (NuGet publish + PackageReference migration), once secrets/config are ready.

## Work Queue (6+ weeks, safe parallelism)
These are intentionally PR-sized and scoped to avoid merge conflicts. Each item lists “hot files” to prevent two agents editing the same core surfaces.

### ✅ W1 — Common token protection (finish + ship) (done)
- Goal: treat `IStringProtector` as the canonical at-rest string protection facade and delete custom crypto in at least one downstream repo within 1–2 follow-up PRs.
- Hot files: `src/Security/TokenProtection/*`, `src/Extensions/ServiceCollectionExtensions.cs`, `docs/dev-guide/TOKEN_PROTECTION.md`.
- Acceptance: `dotnet test -c Release --no-build` passes; a downstream repo (AppleMusicarr) removes its custom protector.

### ✅ W2 — AppleMusicarr correctness (plugin.json-only + crypto migration) (done)
- Goal: keep AppleMusicarr net8-only and remove legacy packaging debt while converging encryption to `IStringProtector` (with migration + deletion).
- Hot files: `applemusicarr/src/AppleMusicarr.Plugin/plugin.json`, `applemusicarr/src/AppleMusicarr.Plugin/Security/**`, `applemusicarr/scripts/*`.
- Acceptance: `dotnet test applemusicarr/AppleMusicarr.sln -c Release` passes; scripts validate `plugin.json` only.

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

### W7 — CI parity and repo settings standardization
- Goal: make “fresh clone → CI green” consistent across plugin repos without manual repo setting drift.
- Hot files: `.github/workflows/**`, `.github/actions/**` (and repo settings like workflow permissions, required secrets).
- Acceptance: each plugin repo has the same multi-plugin smoke test wrapper, and missing secrets fail fast with a clear message (not a cryptic checkout error).

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
