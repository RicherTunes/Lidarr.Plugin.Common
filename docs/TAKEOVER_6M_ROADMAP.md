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

**Implementation Status:** ~90% in `queue-triage-mvp` worktree.

- Exists: `ReviewQueueActionHandler` (applytriage, getaudit, rollbacktriage), `ReviewActionAuditService` (JSONL persistence, idempotency keys), `RecommendationTriageAdvisor` (confidence scoring, risk assessment)
- Missing: `getrollbackoptions` endpoint, DI wiring for 90-day retention default, redaction test suite

---

### Month 2 / Phase 13: Local Verification Parity (Mar 16 - Apr 12)

**Goal:** All 4 plugins have one-command local merge validation via `verify-local.ps1`.

**Status:** In Verification -- scripts exist in all repos but phase requires dated evidence before marking complete.

**Exit Criteria (each requires per-repo evidence):**

1. All 4 plugins have `scripts/verify-local.ps1`
2. Scripts delegate to `ext/Lidarr.Plugin.Common/scripts/local-ci.ps1`
3. Pipeline covers: extract -> build -> package -> closure -> E2E
4. Documented in each repo's CLAUDE.md

**Evidence Block (to be filled during verification):**

```
Tidalarr:      pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests  -> [date] [exit code] [summary]
Qobuzarr:      pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests  -> [date] [exit code] [summary]
AppleMusicarr: pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests  -> [date] [exit code] [summary]
Brainarr:      pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests  -> [date] [exit code] [summary]
Common local-ci.ps1 exists:                                            -> [date] [ls output]
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

**Implementation Status:** ~30%.

- Exists: Basic scoring in `RecommendationTriageAdvisor` (risk 0-6+ scale, bands high/medium/low)
- Missing: Per-provider calibration, golden fixtures, Common contracts, before/after measurement

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

**Implementation Status:** ~50%.

- Exists: `LibraryGapPlannerService` (genre diversification, era gaps, era balance, priority/confidence scoring, `LibraryGapPlanItem` with WhyNow/ExpectedLift fields)
- Missing: Budget constraints, simulate/apply flow, monotonicity tests

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

**Implementation Status:** ~95% in `queue-triage-mvp` worktree.

- Exists: ReviewQueueService, triage workflow, UI action wiring, idempotent operations with audit trail
- Missing: Explainer endpoints, batch caps/cooldowns, operator identity field

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

**Implementation Status:** All 3 classes exist with full test suites behind feature flags / not wired into DI.

- `EnhancedRecommendationCache` (808 lines): 3-level caching, LRU eviction, TTL, metrics
- `SecureStructuredLogger` (776 lines): Auto-masking, correlation IDs, performance tracking
- `ModelRegistryLoader` (684 lines): HTTP registry with ETag, multi-level fallback, SHA256 integrity

**Execution Risk:** Logger/cache wiring can affect metrics and triage behavior. Parallelizable with earlier phases but parity gates are mandatory before merge.

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

| KPI | Formula | Baseline | Phase 17 Target |
|-----|---------|----------|-----------------|
| Flake rate (7-day) | `(failed-then-rerun-pass tests) / (total tests)` over 7-day nightly window | TBD | < 1% |
| CI pass rate | `(green workflow runs) / (total workflow runs)` over 7-day window | TBD | >= 98% |
| Audit replay rate | `(batches with complete replay capability) / (total applied batches)` | N/A | 100% |
| p95 triage latency | 95th percentile of `(triage end - triage start)` per item, in integration tests | N/A | < 500ms |
| Parity drift | `(plugins with divergent contract implementations) / (total plugins)` per contract | TBD | 0% |
| Confidence calibration error | `abs(predicted accept rate - actual accept rate)` averaged across confidence bands | N/A | < 15% |

---

## Copy/Paste Takeover Prompt

> "Own ecosystem delivery across Brainarr, Qobuzarr, Tidalarr, AppleMusicarr, and Common. Keep Common thin; plugin-specific behavior stays in plugin repos. Execute month roadmap in order, using small atomic PRs with tests and local verification when CI billing is blocked. Maintain apply/simulate/audit/rollback safety for all action endpoints. Report weekly KPI deltas: flake rate, CI pass rate, parity drift, and debt burn-down."

---

*Created: 2026-02-16*
*Last updated: 2026-02-16*
