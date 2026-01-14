# Remaining Parity Backlog (Authoritative)

This is the single checklist for finishing cross-plugin parity without chasing fake symmetry.

Rule: each `lidarr.plugin.common/` addition must be justified by deleting real duplicated code in at least one plugin within 1–2 follow-up PRs (or be a pure safety/correctness improvement with no downstream duplication target).

## P0 — Footguns & correctness

- [ ] HTTP logging safe-by-default (Common): `BuildForLogging()` must never emit credential-bearing query values.
  - Status: in review (Common PR `#273`).
  - Acceptance: tests cover token-like keys/values and URL-encoded leak vectors.
- [ ] Manifest `entryPoints` validation (Common tooling): opt-in check that manifest entry points resolve to real types in built assemblies.
  - Status: in review (Common PR `#274`).
  - Acceptance: `tools/ManifestCheck.ps1 -ValidateEntryPoints` fails with stable error codes (`ENT000/ENT001/ENT002`).
- [ ] AppleMusicarr manifest reality check (AppleMusicarr): `manifest.json` entry points reference real net8 build types.
  - Depends on: Common PR `#274`.
  - Acceptance: AppleMusicarr CI runs `tools/ManifestCheck.ps1 -ValidateEntryPoints` and passes; add a guard test so this never regresses.

## P1 — Delete clones / drift reducers

- [ ] Qobuzarr: delete local `PreviewDetectionUtility` clone and use Common everywhere.
  - Status: in review (Qobuzarr PR `RicherTunes/Qobuzarr#156`).
  - Acceptance: `qobuzarr/src/Utilities/PreviewDetectionUtility.cs` removed; tests reference `Lidarr.Plugin.Common.Utilities.PreviewDetectionUtility`.
  - Guard: a test fails if the clone file reappears.
- [ ] Brainarr: resilience split-brain: characterize circuit breaker behavior, then delete the duplicate implementation.
  - Acceptance: characterization tests exist before deletion; runtime behavior stays stable.

## P2 — Security + shared primitives (safe surfaces only)

- [ ] Protected-string façade (Common): public, hard-to-misuse “protected string” API with a stable prefix format and `TryUnprotect`.
  - Acceptance: round-trip + corrupt-payload tests; no raw crypto primitives exposed publicly.
- [ ] Migrate AppleMusicarr off custom crypto (after façade exists).
  - Acceptance: backward-read strategy documented (if needed); no secrets logged.
- [ ] Sanitization primitives (Common, not policy): reusable helpers for safe display/log output (control chars, whitespace, query redaction).
  - Acceptance: tests demonstrate no over-sanitization of artist/title text.    

## P3 — Optional framework adoption (only when it deletes duplication)

- [ ] Tidalarr: adopt `StreamingPlugin` only if it deletes DI/wiring duplication without breaking legacy wrappers.
- [ ] Qobuzarr: add a `StreamingPlugin` entrypoint only if it enables real shared behaviors (don’t do it for symmetry).
