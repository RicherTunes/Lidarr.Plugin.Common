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
| Token/secret protection | ⚠️ In progress | ✅ OAuth tokens | ✅ Session cache | N/A | ❌ Custom crypto |
| Hosting standard (`StreamingPlugin<>`) | ✅ Available | ⚠️ In progress | ❌ Missing | ✅ Bridge | ✅ |
| Resilience standard | ✅ Available | ✅ | ✅ | ⚠️ Needs migration | N/A |

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
- **Agent D (Hosting)**: Qobuzarr `StreamingPlugin<>` entrypoint (non-breaking).
- **Agent E (Resilience)**: Brainarr breaker characterization + Common semantics gap analysis.

## Known Blockers / External Dependencies

- Upstream Lidarr AssemblyLoadContext lifecycle issues can affect multi-plugin loading in certain images; keep E2E “best-effort” mode documented in `docs/MULTI_PLUGIN_SMOKE_TEST.md`.

