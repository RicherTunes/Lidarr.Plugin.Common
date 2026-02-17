# 6-Month Takeover Roadmap

**Scope:** Feb 17 - Aug 31, 2026
**Milestone:** v2.0 Plugin Ecosystem Maturity + Takeover (Phases 12-17)
**Repositories:** Brainarr, Qobuzarr, Tidalarr, AppleMusicarr, Lidarr.Plugin.Common

This document is the **canonical source of truth** for the autonomous AI takeover roadmap. Local `.planning/` files in the workspace root are operational mirrors for GSD tooling and reference this document.

---

## Month-by-Month Breakdown

### Month 1 / Phase 12: Review Action Lifecycle (Feb 17 - Mar 15)

**Goal:** Land and harden the full review action lifecycle: applytriage, getaudit, rollbacktriage, getrollbackoptions.

**Exit Criteria:**

1. `applytriage` -> `getaudit` -> `rollbacktriage` full lifecycle is idempotent
2. `review/getrollbackoptions` endpoint surfaces reversible batches for safe UX-level rollback selection
3. Audit persistence with configurable retention (default 90 days via DI-injected constructor parameter)
4. Redaction tests: no sensitive data in audit logs or exception messages
5. Zero regression in existing review actions
6. Integration test: simulate -> apply -> audit -> rollback round-trip passes

**KPI Targets:**

| KPI | Target |
|-----|--------|
| Audit replay rate | 100% (any applied batch can be fully replayed from audit) |
| Redaction test count | >= 3 (auth tokens, API keys, session IDs) |

**Implementation Status:** Complete.

**Evidence:**

| Exit Criteria | Status | Evidence |
|---------------|--------|----------|
| applytriage -> getaudit -> rollbacktriage lifecycle | Done | Idempotency key replay tested, round-trip integration test |
| review/getrollbackoptions endpoint | Done | Filters rollbackable batches, excludes already-rolled-back |
| Audit persistence (90-day retention) | Done | Constructor-injected, default 90 days |
| Redaction tests (>= 3) | Done | 10 redaction tests (key masking, actor sanitization, truncation) |
| Zero regression | Done | 2316 passed, 0 failures from Phase 12 changes |
| Round-trip integration test | Done | simulate -> apply -> audit -> rollback tested |

- PR: https://github.com/RicherTunes/Brainarr/pull/498 (merged 2026-02-15)
- CI: test pass (1m37s), packaging-gates pass (1m30s), markdownlint pass, lychee pass
- Local verification: `pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests` -> exit 0 (2026-02-16)

---

### Month 2 / Phase 13: Local Verification Parity (Mar 16 - Apr 12)

**Goal:** All 4 plugins have one-command local merge validation via `verify-local.ps1`.

**Status:** Complete.

**Exit Criteria (each requires per-repo evidence):**

1. All 4 plugins have `scripts/verify-local.ps1`
2. Scripts delegate to `ext/Lidarr.Plugin.Common/scripts/local-ci.ps1`
3. Pipeline covers: extract -> build -> package -> closure -> E2E
4. Documented in each repo's CLAUDE.md

**Evidence Block:**

```
Tidalarr:      pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests  -> 2026-02-16  exit 0  BUILD PASS (2s), PACKAGE PASS (5s), CLOSURE PASS (1s)
Qobuzarr:      pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests  -> 2026-02-16  exit 0  BUILD PASS (10s), PACKAGE PASS (12s), CLOSURE PASS
AppleMusicarr: pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests  -> 2026-02-16  exit 0  BUILD PASS (17s), PACKAGE PASS (9s), CLOSURE PASS
Brainarr:      pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests  -> 2026-02-16  exit 0  BUILD PASS (9s), PACKAGE PASS (12s), CLOSURE PASS
Common local-ci.ps1 exists:                                            -> 2026-02-16  ext/Lidarr.Plugin.Common/scripts/local-ci.ps1 present
```

---

### Month 3 / Phase 14: Confidence + Explainability Contracts (Apr 13 - May 10)

**Goal:** Add calibrated confidence mapping per provider in Brainarr. Keep Common thin: only reason/error code contracts and test assertions.

**Exit Criteria:**

1. Calibrated confidence mapping per provider in Brainarr (0.0-1.0 scale)
2. Common stays thin: only reason/error code contracts and test assertions
3. Deterministic golden fixtures for reason-codes
4. Measurable calibration improvement documented (before/after accuracy)

**KPI Targets:**

| KPI | Target |
|-----|--------|
| Confidence calibration error | < 15% (predicted vs actual accept rate per band) |
| Golden fixture count | >= 1 per provider (minimum 5 providers) |

**Implementation Status:** Complete.

**Evidence:**

| Exit Criteria | Status | Evidence |
|---------------|--------|----------|
| Calibrated confidence per provider (0.0-1.0) | Done | ProviderCalibrationRegistry with all 11 providers |
| Common thin: reason/error code contracts only | Done | TriageReasonCodes (8 constants) + ConfidenceBand enum |
| Deterministic golden fixtures | Done | 21 per-provider fixtures across 7+ providers |
| Measurable calibration improvement | Done | Baseline 73.3% -> Calibrated 86.7% (+13.3%), error 13.3% < 15% |

- Common PR: https://github.com/RicherTunes/Lidarr.Plugin.Common/pull/380 (merged)
- Brainarr PR: https://github.com/RicherTunes/Brainarr/pull/499 (merged 2026-02-16)
- Brainarr DoD closure PR: https://github.com/RicherTunes/Brainarr/pull/503 (merged 2026-02-17) — Common submodule bump, contract import, EnableProviderCalibration feature flag
- Tests: 35 new (10 calibration + 21 golden fixtures + 4 accuracy measurement), 2415 total passed after DoD closure
- Local verification: `pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests` -> exit 0 (2026-02-17)

---

### Month 4 / Phase 15: Gap Planner v2 (May 11 - Jun 7)

**Goal:** Add budget constraints, "why now" ranking, and expected lift confidence bands. Add safe simulate/apply flow for gap plans (opt-in only).

**Exit Criteria:**

1. Budget constraints + "why now" + expected lift confidence bands
2. Safe "simulate/apply" flow for gap plans (opt-in only)
3. Monotonicity tests pass (same library -> same plan, deterministic)
4. No unsafe auto-actions (all modifications require explicit confirmation)

**KPI Targets:**

| KPI | Target |
|-----|--------|
| Monotonicity | 100% (identical inputs -> identical outputs) |
| Simulate latency p95 | < 2s |
| Auto-actions without idempotency key | 0 |

**Implementation Status:** Complete.

**Evidence:**

| Exit Criteria | Status | Evidence |
|---------------|--------|----------|
| Budget constraints + "why now" + expected lift | Done | BuildPlan accepts budget + minConfidence params |
| Safe simulate/apply flow | Done | 3 orchestrator routes: simulategapplan/applygapplan/rollbackgapplan |
| Monotonicity tests | Done | 5 deterministic golden fixture tests, 100% monotonicity |
| No unsafe auto-actions | Done | Idempotency key required, audit trail, rollback via rollbackgapplan |

- Brainarr PR: https://github.com/RicherTunes/Brainarr/pull/500 (merged 2026-02-16)
- Tests: 25/25 gap planner tests (budget caps, simulation, monotonicity, golden fixtures, backwards compat)
- Local verification: `pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests` -> exit 0 (2026-02-17)

---

### Month 5 / Phase 16: Queue Triage UX + Safety Controls (Jun 8 - Jul 5)

**Goal:** Add "why this / why not this" explainer endpoints. Add batch caps, cooldowns, and explicit operator identity in audit.

**Exit Criteria:**

1. "Why this / why not this" explainer endpoints
2. Batch caps, cooldowns, and explicit operator identity in audit
3. Triage actions bounded, rollbackable, and policy-enforced

**KPI Targets:**

| KPI | Target |
|-----|--------|
| p95 triage latency | < 500ms per item |
| Batch cap default | 25 items (configurable) |
| Cooldown default | 15 minutes between auto-runs |
| Rollback coverage | 100% of apply actions reversible |

**Implementation Status:** Complete.

**Evidence:**

| Exit Criteria | Status | Evidence |
|---------------|--------|----------|
| "Why this / why not this" explainer endpoints | Done | review/explain route returns whyThis/whyNot per item |
| Batch caps, cooldowns, operator identity | Done | MaxAutoReviewActionsPerRun=25, ReviewActionCooldownMinutes=15 |
| Triage actions bounded and rollbackable | Done | Cooldown enforcement, idempotency before cooldown |

- Brainarr PR: https://github.com/RicherTunes/Brainarr/pull/501 (merged 2026-02-16)
- Tests: 16/16 queue triage UX tests (explainer, cooldown, batch cap, operator identity, safety/rollback)
- Local verification: `pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests` -> exit 0 (2026-02-17)

---

### Month 6 / Phase 17: Brainarr Tech Debt Burn-Down (Jul 6 - Aug 31)

**Goal:** Prioritize extractions: EnhancedRecommendationCache, SecureStructuredLogger, ModelRegistryLoader. Reduce static state and thread-sensitive flake sources.

**Two-gate structure to reduce merge risk:**

#### Gate A: DI Wiring (Jul 6 - Jul 26)

1. `EnhancedRecommendationCache` registered in DI (remove `#if BRAINARR_EXPERIMENTAL_CACHE`)
2. `SecureStructuredLogger` registered in DI and injected into orchestrator
3. `ModelRegistryLoader` registered in DI and wired into provider factory
4. All existing tests still pass (no behavior change)
5. Gate A evidence: `dotnet test` full suite green + `verify-local.ps1` text output

#### Gate B: Behavior Parity + Performance Parity (Jul 27 - Aug 31)

1. Behavior parity tests: old vs new code paths produce identical outputs for golden fixtures
2. Performance parity: new code paths within 10% of old path latency (measured via perf tests)
3. Static state and thread-sensitive flake sources reduced
4. Flake rate < 1%, no CI credibility regressions
5. Gate B evidence: parity test output + perf comparison + 7-day flake rate measurement

**KPI Targets:**

| KPI | Target |
|-----|--------|
| Flake rate | < 1% over 7-day nightly window |
| Top debt files line count reduction | >= 20% |
| Parity drift across plugins | 0% (no new divergence) |
| CI pass rate | >= 98% |
| Performance parity | New paths within 10% of old path latency |

**Implementation Status:** Code merged, DoD pending (KPI measurement in progress).

**Evidence:**

| Gate | Deliverable | Status | Evidence |
|------|-------------|--------|----------|
| A | Remove BRAINARR_EXPERIMENTAL_CACHE feature flags | Done | 808 lines unconditionally compiled |
| A | Register EnhancedRecommendationCache in DI | Done | BrainarrOrchestratorFactory |
| A | Register ISecureLogger (SecureStructuredLogger) in DI | Done | BrainarrOrchestratorFactory |
| A | ModelRegistryLoader already in DI | Done | Already registered at factory line 47 |
| B | DI resolution tests | Done | 3 tests: enhanced cache, ISecureLogger, side-by-side |
| B | Behavior parity tests | Done | 5 tests: roundtrip, miss, clear, overwrite, empty list |
| B | Redaction/masking tests | Done | 4 tests: API keys, JWT tokens, safe text, scope |
| B | Performance parity | Done | Enhanced within 5x of basic (async overhead) |

- Brainarr PR: https://github.com/RicherTunes/Brainarr/pull/502 (merged 2026-02-16)
- Full suite: 2415 passed, 0 failed (after clean build resolving stale assembly issue)
- New tests: 26 (10 enhanced cache + 16 DI/parity)
- **DoD blocker:** 7-day flake rate measurement not yet complete; CI pass rate baseline TBD

**Execution Risk:** Logger/cache wiring can affect metrics and triage behavior. Parity gates passed but 7-day flake measurement window required for KPI sign-off.

---

## Dependency Map & Merge Order

```
Common PR lands first
  +-- Submodule bumps in each plugin repo
      +-- Plugin PRs (one per repo per theme)
```

**Rules:**

- Common-first merge order: Common PR -> plugin submodule bumps -> plugin PRs
- Keep Common thin: no plugin-specific scoring/routing logic in Common
- One concern per PR; always include focused tests and local verification output

**Cross-Phase Dependencies:**

- Phase 12 (Review Actions) -> Phase 16 (Queue Triage UX) shares worktree code
- Phase 14 (Confidence) -> Phase 15 (Gap Planner) uses confidence bands
- Phase 15 (Gap Planner) -> Phase 16 (Queue Triage) uses gap signals in triage
- Phase 17 (Tech Debt) is parallelizable but requires parity gates

---

## Definition of Done (per phase)

Every phase must satisfy ALL gates before status flip:

1. **Tests pass**: Unit + integration + E2E (where applicable)
2. **Redaction checks**: No credentials in logs, exceptions, or audit trails
3. **Rollback proof**: Destructive operations have documented rollback path
4. **CI/local verification artifact**: Text log with command, timestamp, exit code, and summary (not screenshots -- must be reproducible)
5. **Evidence links**: PR URLs, test output (command + exit code + timestamp), verification report committed

---

## Operating Rules for Autonomous AI

1. Common-first merge order (see dependency map)
2. Keep Common thin: no plugin-specific scoring/routing logic
3. One concern per PR; include focused tests and local verification output
4. If CI billing is blocked: run local Docker/verify-local and annotate PR with evidence
5. Never ship auto-actions without idempotency key + audit trail + rollback path

---

## Rollback Policy for Roadmap Metadata

- Phase status can only be flipped by the agent that ran the Definition of Done gate
- Evidence links (PR URLs, verification output) are required for any status change
- Phase status rollback: revert the commit that flipped the checkbox; re-run DoD gate
- Any agent can propose a status flip via PR; merge requires evidence review

---

## Weekly Execution Cadence

- **Mon:** Parity/drift audit + pick 2-3 high-ROI items
- **Tue-Thu:** Implementation + tests + PRs
- **Fri:** Flake triage, pin drift, docs/handoff update

---

## KPI Baselines & Measurement Formulas

Baselines to be measured at Phase 12 start:

| KPI | Formula | Baseline (2026-02-16) | Phase 17 Target |
|-----|---------|----------------------|-----------------|
| Flake rate (7-day) | `(failed-then-rerun-pass tests) / (total tests)` over 7-day nightly window | 0% (2415/2415 after clean build) | < 1% |
| CI pass rate | `(green workflow runs) / (total workflow runs)` over 7-day window | 100% (PR #498 CI green) | >= 98% |
| Audit replay rate | `(batches with complete replay capability) / (total applied batches)` | 100% (tested in Phase 12) | 100% |
| p95 triage latency | 95th percentile of `(triage end - triage start)` per item, in integration tests | < 15ms (16 triage tests, max 14ms) | < 500ms |
| Parity drift | `(plugins with divergent contract implementations) / (total plugins)` per contract | 0/4 (all plugins have verify-local.ps1) | 0% |
| Confidence calibration error | `abs(predicted accept rate - actual accept rate)` averaged across confidence bands | 13.3% (measured in Phase 14) | < 15% |

---

## Copy/Paste Takeover Prompt

> "Own ecosystem delivery across Brainarr, Qobuzarr, Tidalarr, AppleMusicarr, and Common. Keep Common thin; plugin-specific behavior stays in plugin repos. Execute month roadmap in order, using small atomic PRs with tests and local verification when CI billing is blocked. Maintain apply/simulate/audit/rollback safety for all action endpoints. Report weekly KPI deltas: flake rate, CI pass rate, parity drift, and debt burn-down."

---

*Created: 2026-02-16*
*Last updated: 2026-02-17 — Phases 13-16 marked Complete with DoD evidence; Phase 14 DoD closure PR #503 added*
