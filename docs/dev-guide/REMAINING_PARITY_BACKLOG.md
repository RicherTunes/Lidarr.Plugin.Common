# Remaining ecosystem parity backlog

This page is the authoritative backlog for parity work across Brainarr, Qobuzarr, Tidalarr, AppleMusicarr, and Lidarr.Plugin.Common.
It complements `../ECOSYSTEM_PARITY_ROADMAP.md` (packaging/E2E contracts) by focusing on code duplication and correctness.

> **Note:** StreamingPlugin adoption is optional. Only do it when it measurably deletes duplication (for example, hand-rolled `IPlugin` wiring).

## Parity definition (what “parity” means here)

Must be shared in Lidarr.Plugin.Common:

- Filename/path rules and sanitization utilities
- Payload validation
- E2E test infrastructure and error codes
- Token protection and on-disk conventions
- CI drift detection (parity lint) and reusable security primitives

Must stay plugin-specific:

- Provider auth/signing/scraping and provider-specific rate-limit heuristics
- Provider DTOs/mappers
- ML, AI prompting, provider policy decisions

## Operating rules (avoid drift)

- Treat `Lidarr.Plugin.Common` as the platform for cross-cutting primitives; keep it thin by default.
- When a Common change ships, bump the Common submodule in all plugins as one sweep.
- Import lists remain legacy-host concepts; do not force schema/entrypoint uniformity beyond schema validation until Abstractions models import lists.

## Backlog

### P0 — Leaks and footguns

- [x] Common: make request logging safe-by-default (no query values in log URLs)
  - Evidence: `StreamingApiRequestBuilder.BuildForLogging()` used `BuildUrl()` which included raw query values.
  - Change: `lidarr.plugin.common/src/Services/Http/StreamingApiRequestBuilder.cs` now uses a log-safe URL builder and value masking.
  - Acceptance: `BuildForLogging()` never emits query values in `StreamingApiRequestInfo.Url`; tests prove redaction (`lidarr.plugin.common/tests/StreamingApiRequestBuilderLoggingTests.cs`).

- [x] AppleMusicarr: fix manifest entrypoint mismatch (ImportList type must exist in net8 output)
  - Evidence: `applemusicarr/src/AppleMusicarr.Plugin/manifest.json` references `AppleMusicarr.Plugin.Importing.LidarrAppleMusicImportList`, but `applemusicarr/src/AppleMusicarr.Plugin/Importing/LidarrAppleMusicImportList.cs` is `#if NET6_0`-guarded while `applemusicarr/src/AppleMusicarr.Plugin/AppleMusicarr.Plugin.csproj` targets `net8.0`.
  - Acceptance: plugin build output contains the manifest’s `implementation` type; `applemusicarr/tests/AppleMusicarr.Plugin.Tests/Manifest/ManifestEntrypointsTests.cs` asserts manifest entrypoints resolve against the built assembly.

- [ ] Common: add a public token-protection façade for protected strings (stable prefix + versioning)
  - Evidence: `lidarr.plugin.common/src/Security/TokenProtection/TokenProtectorFactory.cs` is `internal`.
  - Acceptance: public API does not expose raw primitives; includes round-trip and “wrong prefix” tests; `docs/dev-guide/TOKEN_PROTECTION.md` documents the string format and migration strategy.

### P1 — Guardrails (prevent reintroduction)

- [ ] AppleMusicarr: run Common parity lint in CI
  - Evidence: no parity-lint usage in `applemusicarr/.github/workflows/*.yml`.
  - Acceptance: CI runs `ext/lidarr.plugin.common/scripts/parity-lint.ps1` (or equivalent) and fails on drift.

- [ ] Common: add parity-lint rules for known clone hotspots
  - Evidence: `qobuzarr/src/Utilities/PreviewDetectionUtility.cs` duplicates `lidarr.plugin.common/src/Utilities/PreviewDetectionUtility.cs`.
  - Acceptance: parity lint fails when clone files reappear; baselines require owner + expiry + issue link; CI fails on expired baselines.

### P2 — Delete clones / drift reducers

- [ ] Qobuzarr: remove `PreviewDetectionUtility` clone and use Common everywhere
  - Evidence: `qobuzarr/src/Utilities/PreviewDetectionUtility.cs` duplicates `lidarr.plugin.common/src/Utilities/PreviewDetectionUtility.cs`.
  - Acceptance: clone deleted; all call sites reference `Lidarr.Plugin.Common.Utilities.PreviewDetectionUtility`; tests pass.

- [ ] Brainarr: consolidate duplicate circuit breakers behind one implementation
  - Evidence: `brainarr/Brainarr.Plugin/Resilience/CircuitBreaker.cs` and `brainarr/Brainarr.Plugin/Services/Resilience/CircuitBreaker.cs`.
  - Acceptance: characterization tests cover open/half-open thresholds and timeout semantics; only one circuit breaker remains; provider error handling stays stable.

### P3 — Security + correctness

- [ ] AppleMusicarr: migrate off custom `DataProtector` to Common token protection façade
  - Evidence: `applemusicarr/src/AppleMusicarr.Plugin/Security/DataProtector.cs` stores the AES key unwrapped on non-Windows and uses `enc:v1:` values.
  - Acceptance: existing `enc:v1:` values remain readable (migration path); new writes use Common format; no plaintext key material stored on disk.

- [ ] Common: extract reusable sanitization primitives (keep provider policy local)
  - Evidence: `qobuzarr/src/Security/InputSanitizer.cs` contains richer normalization/redaction than `lidarr.plugin.common/src/Security/Sanitize.cs`; Common `InputSanitizer` is obsolete (`lidarr.plugin.common/src/Security/InputSanitizer.cs`).
  - Acceptance: new Common helpers cover shared needs (display encoding, URL/query redaction, control/zero-width normalization) and are adopted by at least one plugin without behavior regressions.

### P4 — Optional (only when it deletes duplication)

- [ ] Tidalarr: consider StreamingPlugin adoption only if it deletes manual `IPlugin` wiring
  - Evidence: `tidalarr/src/Tidalarr/Integration/TidalarrPlugin.cs` vs `tidalarr/src/Tidalarr/Integration/TidalModule.cs` (`StreamingPluginModule`).
  - Acceptance: measurable deletion of glue code; no changes to legacy wrapper behavior (`tidalarr/src/Tidalarr/Integration/LidarrNative/*`); packaging/E2E gates unchanged.

- [ ] Qobuzarr: consider StreamingPlugin entrypoint only if it unlocks shared behavior without copying
  - Evidence: current entrypoints are legacy base classes (`qobuzarr/src/Indexers/QobuzIndexer.cs`, `qobuzarr/src/Download/Clients/QobuzDownloadClient.cs`).
  - Acceptance: PR shows concrete removed duplication; otherwise, explicitly skip.

## Definition of done (this backlog)

- [ ] No duplicated preview-detection/token/sanitization plumbing across plugins
- [ ] Token storage uses a shared, documented format with a migration path
- [ ] Shared security primitives live in Common; provider policy stays local
- [ ] Parity lint prevents re-introducing known clones
- [ ] Request logging does not leak secrets by default
