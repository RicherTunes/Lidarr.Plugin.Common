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

## Milestones (PR-Sized)

### M1 — Remove obvious clones (quick wins)
**Target:** Delete plugin-local copies of utilities that already exist in Common.

- [ ] Qobuzarr: delete any remaining preview/sample detection clone(s) (if reintroduced) and use `Lidarr.Plugin.Common.Utilities.PreviewDetectionUtility` everywhere.
  - Acceptance: `dotnet test qobuzarr/Qobuzarr.sln -c Release` passes.
- [ ] AppleMusicarr: remove dead net6-only legacy ImportList bridge code if it is no longer referenced by manifests or builds.
  - Acceptance: `dotnet build applemusicarr/src/AppleMusicarr.Plugin/AppleMusicarr.Plugin.csproj -c Release` passes; manifests match compiled types.

### M2 — Public token protection facade in Common (enables deletion)
**Target:** Stop downstream plugins from implementing their own encryption primitives.

- [ ] Common: expose a small, stable public API for protecting strings at rest.
  - Deliverable: `IStringProtector` (or similar) + versioned format docs + tests.
  - Acceptance: Common tests pass; no breaking changes to existing consumers.
- [ ] AppleMusicarr follow-up: replace `AppleMusicarr.Plugin.Security.DataProtector` with the Common facade.
  - Acceptance: existing encrypted values can be read (dual-read/migration), and new writes use the Common format; E2E secrets redaction still holds.

### M3 — Manifest/entrypoint reality checks (correctness ROI)
**Target:** Catch “manifest points at a type that does not exist in net8 build” problems early.

- [ ] Common: extend tooling (or add a testkit helper) that can validate `plugin.json` / `manifest.json` entry points against an assembly.
  - Acceptance: a unit test fails on a known-bad fixture; passes on real plugin artifacts.
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
- **AI A:** Common M2 (token protection facade + tests).
- **AI B:** AppleMusicarr M2 follow-up (migrate DataProtector → Common facade).
- **AI C:** Brainarr M5 (resilience consolidation) with characterization tests first.
- **AI D:** M3 tooling (entrypoint reality check) + AppleMusicarr manifest/docs cleanup.

## Definition of Done (Pragmatic “100% parity”)
- No duplicated generic utilities across plugin repos (preview detection, filename sanitization, payload validation, common retry/breaker primitives).
- Token protection uses a Common public facade; no plugin ships custom crypto primitives.
- Hosting pattern is consistent for net8 plugin hosts: module + `StreamingPlugin` (legacy adapters allowed but thin).
- E2E bootstrap can configure and validate Qobuzarr + Tidalarr + Brainarr (schema/importlist) without UI steps (using env vars/secrets).
- Parity lint stays quiet on `main`, and any baseline exception has an owner + expiry + issue link.
