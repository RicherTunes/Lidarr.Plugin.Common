# H2 2026 Takeover Roadmap

**Scope:** March 1 - August 31, 2026
**Milestone:** v2.1 Ecosystem Reliability + Intelligence (Phases 18-23)
**Repositories:** Brainarr, Qobuzarr, Tidalarr, AppleMusicarr, Lidarr.Plugin.Common

This document is the **canonical source of truth** for the H2 2026 roadmap. It continues from `TAKEOVER_6M_ROADMAP.md` (Phases 12-17, Feb-Aug 2026). Local `.planning/` files in the workspace root are operational mirrors for GSD tooling and reference this document.

---

## Phase Summary

| Phase | Name | Window | Dependency |
|-------|------|--------|------------|
| 18 | Reliability Baseline Lock | Mar 1 - Mar 31 | Phase 17 complete |
| 19 | Confidence Calibration v1 | Apr 1 - Apr 30 | Phase 18 complete |
| 20 | Explainability Contracts | May 1 - May 31 | Phase 19 complete |
| 21 | Gap Planner v2 Execution Safety | Jun 1 - Jun 30 | Phase 20 complete |
| 22 | Cross-Plugin Parity Sweep | Jul 1 - Jul 31 | Phase 21 complete |
| 23 | Brainarr Debt Burn-Down v2 | Aug 1 - Aug 31 | Phase 22 complete |

---

## Month-by-Month Breakdown

### Month 1 / Phase 18: Reliability Baseline Lock (Mar 1 - Mar 31)

**Goal:** Freeze KPI methodology, evidence templates, and CI health standards so that all subsequent phases measure against a stable, auditable baseline.

**Exit Criteria:**

1. KPI definitions document committed to Common (`docs/KPI_DEFINITIONS.md`) with formulas, measurement frequency, and acceptable ranges
2. Evidence template standardized: every phase gate produces a structured artifact with command, timestamp, exit code, summary, and submodule SHA table
3. CI pass rate >= 98% sustained over 14 consecutive days on Brainarr main
4. Flake rate < 0.5% over 14-day window (tighter than Phase 17's 1%)
5. All billing-blocked repos have standardized local verification annotations
6. Phase 17 officially closed with full 7-day evidence (carried over from Feb 23)

**KPI Targets:**

| KPI | Formula | Target |
|-----|---------|--------|
| CI pass rate (14-day) | `green runs / total runs` over 14-day window | >= 98% |
| Flake rate (14-day) | `failed-then-rerun-pass / total tests` over 14-day window | < 0.5% |
| Evidence template compliance | `phases with valid artifacts / total completed phases` | 100% |
| KPI definition coverage | `KPIs with committed formulas / total tracked KPIs` | 100% |

**Implementation Notes:**
- Phase 18 kickoff already started (CorrelationScope removal, PR #505 merged 2026-02-17)
- Close Phase 17 on Feb 23 with final flake evidence, then begin Phase 18 formally on Mar 1
- Consolidate all KPI formulas from PHASE_COMPLETE.md, KPI_WEEKLY.md, and this roadmap into one canonical `KPI_DEFINITIONS.md`
- Establish automated or semi-automated daily flake capture process

---

### Month 2 / Phase 19: Confidence Calibration v1 (Apr 1 - Apr 30)

**Goal:** Extend the Phase 14 calibration foundation into production-grade, provider-level accuracy tracking with measurable improvement over baseline.

**Exit Criteria:**

1. Per-provider calibration profiles stored and versioned (not hardcoded)
2. Calibration self-test suite: golden fixtures produce expected confidence bands within tolerance
3. Confidence band accuracy: predicted accept rate within 10% of actual (tighter than Phase 14's 15%)
4. At least 7 providers have calibration profiles (up from 5 in Phase 14)
5. Calibration drift detection: alert or log when observed accuracy deviates > 5% from profile
6. No regression in existing Phase 14 tests

**KPI Targets:**

| KPI | Formula | Target |
|-----|---------|--------|
| Confidence calibration error | `abs(predicted - actual)` avg across bands | < 10% |
| Provider coverage | `providers with calibration profiles / total providers` | >= 7/9 |
| Golden fixture count | total per-provider golden fixtures | >= 30 |
| Drift detection sensitivity | `detected drifts / actual drifts` in test scenarios | 100% |

**Implementation Notes:**
- Build on ProviderCalibrationRegistry (Phase 14) — extend from static profiles to versioned configs
- Add calibration refresh mechanism (re-measure accuracy periodically)
- Keep Common thin: only confidence band enum and reason code contracts remain in Common

---

### Month 3 / Phase 20: Explainability Contracts (May 1 - May 31)

**Goal:** Ship deterministic "why this / why not this" endpoints with reason-codes that are stable across versions and reproducible from golden fixtures.

**Exit Criteria:**

1. `review/explain` endpoint returns structured `whyThis` / `whyNot` per recommendation with reason codes
2. Reason codes are stable constants (registered in Common `TriageReasonCodes` or equivalent)
3. Deterministic: same input -> same reason codes (golden fixture tests)
4. At least 8 distinct reason codes covering: confidence, provider, genre match, era match, library overlap, budget, cooldown, blacklist
5. Explainer response time p95 < 100ms per item
6. No regression in Phase 16 explainer tests

**KPI Targets:**

| KPI | Formula | Target |
|-----|---------|--------|
| Reason code coverage | `distinct reason codes in codebase / required reason codes` | >= 8 |
| Determinism | `golden fixtures with stable output / total golden fixtures` | 100% |
| Explainer p95 latency | 95th percentile response time per item | < 100ms |
| Regression count | `Phase 16 explainer tests failing after Phase 20 changes` | 0 |

**Implementation Notes:**
- Phase 16 already has `review/explain` route — Phase 20 hardens it with contracts and golden fixtures
- Reason codes should be backward-compatible additions, never removals
- Common gets reason code constants; Brainarr implements the explainer logic

---

### Month 4 / Phase 21: Gap Planner v2 Execution Safety (Jun 1 - Jun 30)

**Goal:** Harden the Gap Planner's simulate/apply/rollback flow with budget constraints, idempotency keys, and operator identity so that no gap plan can execute without explicit, auditable authorization.

**Exit Criteria:**

1. Budget constraints enforced: gap plans respect max-spend-per-run and max-items-per-plan limits
2. Idempotency key required for all apply operations (reject duplicates)
3. Operator identity recorded in audit trail for every apply and rollback
4. Simulate is side-effect-free (verified by test: simulate twice -> no state change)
5. Monotonicity: identical library state -> identical gap plan (100% deterministic)
6. Rollback coverage: every apply action has a tested rollback path
7. No regression in Phase 15 gap planner tests

**KPI Targets:**

| KPI | Formula | Target |
|-----|---------|--------|
| Monotonicity | `identical-input pairs with identical output / total pairs` | 100% |
| Simulate latency p95 | 95th percentile of simulate operation | < 2s |
| Rollback coverage | `apply actions with tested rollback / total apply actions` | 100% |
| Budget enforcement | `plans exceeding budget that were rejected / plans exceeding budget` | 100% |
| Idempotency rejection | `duplicate apply attempts correctly rejected / total duplicates` | 100% |

**Implementation Notes:**
- Build on Phase 15 gap planner (simulategapplan/applygapplan/rollbackgapplan routes)
- Budget constraints are new: add MaxItemsPerPlan and MaxCostPerRun configuration
- Operator identity: extend audit record with `operatorId` field (defaults to "ai-agent" or Lidarr user)

---

### Month 5 / Phase 22: Cross-Plugin Parity Sweep (Jul 1 - Jul 31)

**Goal:** Unify shared contracts, remove duplicated plugin-local glue code, and ensure all 4 plugins consume Common's canonical implementations identically.

**Exit Criteria:**

1. Parity lint passes for all 4 plugins (script in Common `scripts/parity-lint.ps1`)
2. Zero plugin-local copies of code that belongs in Common (measured by parity lint)
3. All 4 plugins on identical Common submodule SHA
4. Packaging closure check passes for all 4 plugins (no unexpected DLLs)
5. `verify-local.ps1` passes for all 4 plugins with identical Common version
6. Documentation: each plugin's CLAUDE.md references Common's canonical docs

**KPI Targets:**

| KPI | Formula | Target |
|-----|---------|--------|
| Parity drift | `plugins with divergent implementations / total plugins` | 0/4 |
| Common SHA alignment | `plugins on latest Common SHA / total plugins` | 4/4 |
| Plugin-local duplicate code | `files flagged by parity lint` | 0 |
| Packaging closure | `plugins passing closure check / total plugins` | 4/4 |

**Implementation Notes:**
- Parity lint already exists (`scripts/parity-lint.ps1`, `scripts/tests/Test-ParityLint.ps1`)
- Focus: audit each plugin for local implementations of contracts that should come from Common
- Common-first: any shared code moves to Common first, then plugins consume via submodule bump
- This phase is primarily audit + migration, minimal new feature code

---

### Month 6 / Phase 23: Brainarr Debt Burn-Down v2 (Aug 1 - Aug 31)

**Goal:** Integrate remaining feature-flagged or standalone infrastructure into DI, reduce static state, and improve test isolation — building on Phase 17's foundation.

**Two-gate structure (same pattern as Phase 17):**

#### Gate A: Infrastructure Integration (Aug 1 - Aug 15)

1. Identify remaining feature-flagged code, static singletons, and standalone infrastructure
2. Wire into DI container with interface abstractions
3. All existing tests still pass (no behavior change)
4. Gate A evidence: `dotnet test` full suite green + `verify-local.ps1` output

#### Gate B: Test Isolation + Performance Verification (Aug 16 - Aug 31)

1. Static state audit: enumerate and eliminate remaining shared mutable state between tests
2. Test isolation: no test depends on execution order or shared state from another test
3. Performance parity: new DI paths within 10% of old path latency
4. Flake rate < 0.5% over 7-day window (matching Phase 18 standard)
5. Gate B evidence: parity test output + perf comparison + 7-day flake rate

**KPI Targets:**

| KPI | Formula | Target |
|-----|---------|--------|
| Flake rate (7-day) | `failed-then-rerun-pass / total tests` over 7-day window | < 0.5% |
| Static state instances | `mutable static fields in production code` | 0 (or justified exceptions) |
| Test isolation | `tests that fail when run in random order / total tests` | 0 |
| CI pass rate (7-day) | `green runs / total runs` over 7-day window | >= 99% |
| Performance parity | `new path latency / old path latency` | Within 10% |
| Top debt files line count reduction | `(before - after) / before` | >= 15% |

**Implementation Notes:**
- Phase 17 wired EnhancedRecommendationCache, SecureStructuredLogger, ModelRegistryLoader
- Phase 23 targets remaining candidates (discovered during Phase 17 Gate B audit)
- Use `Interlocked` or `IDisposable` scope patterns instead of static state
- Run tests in random order as a gate (e.g., `dotnet test --shuffle`)

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

- Phase 18 (Reliability) -> all subsequent phases (establishes baseline standards)
- Phase 19 (Calibration) -> Phase 20 (Explainability) uses calibrated confidence in reason codes
- Phase 20 (Explainability) -> Phase 21 (Gap Planner Safety) uses reason codes in gap explanations
- Phase 22 (Parity Sweep) is audit-focused, can overlap with Phase 21 implementation
- Phase 23 (Debt Burn-Down v2) requires Phase 22 parity to avoid divergent cleanup

---

## Definition of Done (per phase)

Every phase must satisfy ALL gates before status flip:

1. **Tests pass**: Unit + integration + E2E (where applicable)
2. **Redaction checks**: No credentials in logs, exceptions, or audit trails
3. **Rollback proof**: Destructive operations have documented rollback path
4. **CI/local verification artifact**: Structured text with command, timestamp, exit code, summary, and submodule SHA table (not screenshots)
5. **Evidence links**: PR URLs, CI run URLs, test output with command + exit code + timestamp
6. **KPI measurement**: All phase KPIs measured and recorded in KPI_WEEKLY.md with formulas
7. **Submodule alignment**: All 4 plugins on same Common SHA, verified with ext-common-sha.txt match

---

## Operating Rules for Autonomous AI

1. Common-first merge order (see dependency map)
2. Keep Common thin: no plugin-specific scoring/routing logic
3. One concern per PR; include focused tests and local verification output
4. If CI billing is blocked: run `verify-local.ps1` and annotate PR with structured evidence (cmd, date, exit, summary)
5. Never ship auto-actions without idempotency key + audit trail + rollback path
6. Phase status can only be flipped after Definition of Done gate evidence is committed
7. Evidence must include both submodule gitlink SHA and ext-common-sha.txt SHA
8. Host-boundary guardrails mandatory for dependency upgrades (no .NET 10 BCL on .NET 8 host)

---

## Rollback Policy

- Phase status can only be flipped by the agent that ran the Definition of Done gate
- Evidence links (PR URLs, verification output) are required for any status change
- Phase status rollback: revert the commit that flipped the checkbox; re-run DoD gate
- Any agent can propose a status flip via PR; merge requires evidence review

---

## Weekly Execution Cadence

- **Mon:** Parity/drift audit + KPI measurement + pick 2-3 high-ROI items
- **Tue-Thu:** Implementation + tests + PRs
- **Fri:** Flake triage, pin drift, docs/handoff update, KPI_WEEKLY.md entry

---

## KPI Baselines & Measurement Formulas

Baselines to be measured at Phase 18 start (after Phase 17 close):

| KPI | Formula | Phase 17 Final | H2 Target |
|-----|---------|---------------|-----------|
| Flake rate (14-day) | `failed-then-rerun-pass / total tests` over 14-day window | TBD (Feb 23) | < 0.5% |
| CI pass rate (14-day) | `green runs / total runs` over 14-day window | TBD (Feb 23) | >= 99% |
| Test count | `dotnet test` total passed in Brainarr | 2416 (Feb 17) | Increasing |
| Parity drift | `divergent plugins / total plugins` per contract | 0/4 | 0/4 |
| Confidence calibration error | `abs(predicted - actual)` avg across bands | 13.3% | < 10% |
| Static state instances | `mutable static fields in production code` | TBD (audit) | 0 |
| Golden fixture count | total per-provider golden fixtures | 21 | >= 30 |

---

## Evidence Template

Every phase closure must include a structured evidence block in this format:

```markdown
## Phase [N] Evidence

**Date:** YYYY-MM-DD
**Commit:** [SHA]
**Branch:** main

### Test Results
cmd:  dotnet test -c Release --no-build
date: YYYY-MM-DD HH:MM TZ
exit: 0
summary: [N] passed, [M] failed, [K] skipped

### Local Verification (per plugin)
Brainarr:
  cmd:  pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests
  date: YYYY-MM-DD HH:MM TZ
  exit: 0
  summary: BUILD PASS, PACKAGE PASS, CLOSURE PASS

[Repeat for Tidalarr, Qobuzarr, AppleMusicarr]

### Submodule State
| Repo | Submodule gitlink SHA | ext-common-sha.txt | Match |
|------|----------------------|-------------------|-------|
| Brainarr | [SHA] | [SHA] | Yes/No |
| Tidalarr | [SHA] | [SHA] | Yes/No |
| Qobuzarr | [SHA] | [SHA] | Yes/No |
| AppleMusicarr | [SHA] | [SHA] | Yes/No |

### KPI Actuals
| KPI | Formula | Value | Target | Met |
|-----|---------|-------|--------|-----|
| ... | ... | ... | ... | ... |

### PR Evidence
- Common: [URL] (merged YYYY-MM-DD)
- Brainarr: [URL] (merged YYYY-MM-DD)
- [Other plugin PRs as applicable]
```

---

## Copy/Paste Takeover Prompt

> "Own ecosystem delivery across Brainarr, Qobuzarr, Tidalarr, AppleMusicarr, and Common. Keep Common thin; plugin-specific behavior stays in plugin repos. Execute H2 2026 roadmap (Phases 18-23) in order, using small atomic PRs with tests and local verification when CI billing is blocked. Maintain apply/simulate/audit/rollback safety for all action endpoints. Enforce host-boundary guardrails for dependency upgrades. Report weekly KPI deltas: flake rate, CI pass rate, calibration error, parity drift, and debt burn-down. Phase closures require structured evidence artifacts with submodule SHA table."

---

*Created: 2026-02-17*
*Continues from: TAKEOVER_6M_ROADMAP.md (Phases 12-17)*
