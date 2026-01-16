# Ecosystem Parity Roadmap

This document tracks parity across the plugin ecosystem (Tidalarr, Qobuzarr, Brainarr, AppleMusicarr) using a **deletion-driven** approach:

- **Thin Common**: `lidarr.plugin.common/` owns shared primitives + guardrails.
- **Delete-or-don’t-add**: any new Common API must delete measurable duplication in ≥1 plugin within ≤2 follow-up PRs.
- **TDD-first**: new contracts land with hermetic tests/golden fixtures.
- **E2E is acceptance**: for runtime changes, validate with `scripts/e2e-runner.ps1 -Gate bootstrap -EmitJson`.

For handoff/parallel work conventions, see `docs/dev-guide/ECOSYSTEM_HANDOFF.md`.

## Current Snapshot

| Area | Common | Tidalarr | Qobuzarr | Brainarr | AppleMusicarr |
|------|--------|----------|----------|----------|---------------|
| Packaging policy | ✅ Canonical | ✅ Adopt | ✅ Adopt | ✅ Adopt | ⚠️ Verify/adopt |
| E2E platform | ✅ Canonical | ✅ | ✅ | ✅ (ImportList) | ⚠️ Partial |
| Token/secret protection | ⚠️ PR pending (secret string façade) | ✅ OAuth tokens | ✅ Session cache | N/A | ❌ Custom crypto (migration pending) |
| Hosting standard (`StreamingPlugin<>`) | ✅ Available | ⚠️ PR pending (migrate `TidalarrPlugin`) | ⚠️ Planned (do not ship stub `IPlugin`) | ✅ Bridge | ✅ |
| Resilience standard | ✅ Available | ✅ | ✅ | ⚠️ Needs migration | N/A |

## Status Board (What’s Actually Pending)

This section exists to prevent “looks done” drift. Each item should be PR-sized and include the acceptance command(s).

### WS1 — AppleMusicarr Correctness + Security

- [ ] **WS1.1 Manifest entrypoint reality**: fix net8 entrypoint mismatch and add a guard test that resolves all configured entrypoint types from the built assembly.
- [ ] **WS1.2 Secret storage deletion**: migrate AppleMusicarr string secrets to the Common secret protection façade and delete bespoke crypto/key helpers (must delete ≥200 LOC within ≤2 PRs after the façade lands).

### WS2 — Abstractions Distribution (Byte-Identical Host ABI)

- [ ] **WS2.1 Publish `Lidarr.Plugin.Abstractions`**: configure `NUGET_API_KEY` in the Common repo and publish a versioned package to NuGet.org.
- [ ] **WS2.2 Migrate consumers**: replace plugin `ProjectReference` → `PackageReference` where feasible and add a CI guard that Abstractions bytes are identical across built zips.

### WS3 — Hosting Convergence (Reduce Drift)

- [ ] **WS3.1 Tidalarr**: migrate `TidalarrPlugin` to `StreamingPlugin<TidalModule, TidalarrSettings>` and delete duplicated hosting/settings wiring.
- [ ] **WS3.2 Qobuzarr**: do **not** ship an `IPlugin` implementation until it is functional; either:
  - (A) implement the full Abstractions indexer/download client path, **or**
  - (B) intentionally keep legacy-only and document that as an exception.

### WS4 — Brainarr Resilience Migration (Not “delete first”)

- [ ] **WS4.1 Characterize current breaker**: lock down Brainarr’s circuit-breaker semantics (provider/model keying, failure classification, timing) with characterization tests.
- [ ] **WS4.2 Migrate or justify**: either migrate Brainarr to the Common breaker (thin extensions allowed only if followed by deletions) or document why Brainarr must remain custom.

### WS5 — Anti-Drift Guardrails

- [ ] **WS5.1 Parity lint**: ensure `scripts/parity-lint.ps1` scans all 4 plugin repos and enforces expiry-backed baselines.
- [ ] **WS5.2 “Contract docs are code”**: keep CI coverage so changes to contract docs run the full test suite (no doc-only bypass).

### WS6 — CI / Multi-Repo Smoke (Ergonomics + Safety)

- [ ] **WS6.1 CROSS_REPO_PAT fail-fast**: reusable workflows should produce a single, explicit failure when `CROSS_REPO_PAT` is missing, with remediation steps.
- [ ] **WS6.2 Standard wrappers**: keep `multi-plugin-smoke-test.yml` wrappers in each plugin repo consistent (paths, schedules, and inputs).

### WS7 — Common Safe-By-Default Logging (Security ROI)

- [ ] **WS7.1 HTTP log redaction**: make request log URLs query-safe by default (no raw query params); add unit tests proving sensitive query keys are redacted.
- [ ] **WS7.2 Deletion follow-up**: delete any plugin-local “URL-for-logging” redaction helpers within ≤2 PRs after WS7.1 lands.

### WS8 — Manifest Tooling (Correctness ROI)

- [ ] **WS8.1 Entrypoint resolution check**: extend manifest tooling to optionally verify that any declared entrypoint types exist in the built net8 assembly.
- [ ] **WS8.2 Apply to AppleMusicarr**: add/enable the check in AppleMusicarr CI so entrypoint mismatches cannot regress.

## Definition Of Done (Parity “As Much As It Makes Sense”)

- [ ] Packaging payload contract enforced everywhere (required/forbidden DLLs).
- [ ] “New Common API” always paired with an imminent deletion PR.
- [ ] AppleMusicarr has no manifest/entrypoint mismatches for net8 builds.
- [ ] Streaming plugins converge on the same hosting entrypoint pattern (or document the intentional exception).
- [ ] Brainarr resilience uses one breaker stack (Common or explicitly documented Brainarr-only).
- [ ] E2E bootstrap runs are reproducible (manifest includes sources/provenance) and failures always have explicit `errorCode` (or `E2E_INTERNAL_ERROR`).

## Canonical Contracts (Do Not Drift)

### Packaging payload
See `docs/PACKAGING.md` for the canonical “MUST SHIP / MUST NOT SHIP” list.

### E2E
- Error codes + structured details: `docs/E2E_ERROR_CODES.md`
- Runner usage: `docs/PERSISTENT_E2E_TESTING.md`

## Workstreams (Weeks-Scale Queue)

Each workstream is intended to be PR-sized and parallelizable across multiple agents.

### WS1 — AppleMusicarr Correctness + Security (Highest ROI)

**Goal**: eliminate real correctness/security risks (manifest mismatches, bespoke crypto).

1. Manifest entrypoint reality check (Common tooling + AppleMusicarr fix)
   - Contract: any referenced entrypoint type must exist in the built net8 assembly.
   - Deletion target: remove net6-only/unused entrypoint references or ship the type for net8.
2. Secret storage migration
   - Adopt the Common secret protection façade for string secrets.
   - Deletion target: remove AppleMusicarr crypto/key management helpers after migration.

### WS2 — Abstractions Distribution (Byte-Identical Host ABI)

**Goal**: prevent “same source, different bytes” Abstractions drift across plugin packages.

1. Publish `Lidarr.Plugin.Abstractions` to NuGet.org (repo secret + tag).
2. Migrate plugins from `ProjectReference` → `PackageReference` where appropriate.
3. Add CI guardrails: fail if Abstractions bytes differ across plugin zips for the same build.

### WS3 — Hosting Convergence (Reduce Drift)

**Goal**: converge on a single settings/DI/hosting pattern without breaking legacy surfaces.

1. Tidalarr: replace manual `IPlugin` plumbing with `StreamingPlugin<TModule, TSettings>` (deletion-driven).
2. Qobuzarr: add a `StreamingPlugin<>` entrypoint for settings + DI parity (keep existing indexer/download classes unchanged initially).

### WS4 — Brainarr Resilience Migration (Not “delete first”)

**Goal**: eliminate split-brain resilience while preserving Brainarr behavior.

1. Characterize the current Brainarr breaker semantics (profiles, keying, what counts as failure).
2. Extend Common breaker semantics only if required (thin additions, must delete Brainarr code quickly).
3. Switch Brainarr orchestrator to the Common breaker (or document why Brainarr must stay custom).

### WS5 — Anti-Drift Guardrails

**Goal**: prevent clone re-introductions and keep parity measurable.

1. Expand `scripts/parity-lint.ps1` to include AppleMusicarr and additional high-signal clone patterns (only after a real deletion PR validates the signal).
2. Add/maintain CI tripwires so “contract docs” are treated as code (tests must run when they change).

## Suggested Agent Allocation (Parallel-Friendly)

- **Agent A (AppleMusicarr manifests)**: entrypoint/type resolution guard + AppleMusicarr fix + tests.
- **Agent B (AppleMusicarr secrets)**: migrate to Common secret protection façade + delete crypto helpers.
- **Agent C (NuGet / ABI)**: publish Abstractions + migrate to PackageReference + add CI byte-identity check.
- **Agent D (Hosting)**: Tidalarr `StreamingPlugin<>` migration + Qobuzarr hosting decision (no stub `IPlugin`).
- **Agent E (Resilience)**: Brainarr breaker characterization + Common semantics gap analysis.
- **Agent F (CI ergonomics)**: reusable workflow hardening (CROSS_REPO_PAT fail-fast, wrapper parity).

## Known Blockers / External Dependencies

- Upstream Lidarr AssemblyLoadContext lifecycle issues can affect multi-plugin loading in certain images; keep E2E “best-effort” mode documented in `docs/MULTI_PLUGIN_SMOKE_TEST.md`.
