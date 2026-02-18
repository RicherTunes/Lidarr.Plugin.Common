# KPI Definitions

Canonical definitions for all Key Performance Indicators tracked across the plugin ecosystem. Each KPI includes its formula, measurement method, acceptable range, and escalation criteria.

This document is the **single source of truth** for KPI methodology. Phase-specific targets may tighten these ranges (e.g., Phase 18 requires flake rate < 0.5% vs the baseline < 1%).

---

## Reliability KPIs

### Flake Rate

| Field | Value |
|-------|-------|
| Formula | `(failed-then-rerun-pass tests) / (total tests)` over measurement window |
| Measurement window | 7-day rolling (default); 14-day for Phase 18+ |
| Measurement method | Run `dotnet test --filter "State!=Quarantined"` daily on main. A test that fails then passes on re-run within the same session counts as one flake. |
| Baseline target | < 1% |
| Phase 18+ target | < 0.5% |
| Escalation | Any flake > 0% triggers immediate investigation. Fix before new feature work. |

### CI Pass Rate

| Field | Value |
|-------|-------|
| Formula | `(green workflow runs) / (total workflow runs)` over measurement window |
| Measurement window | 7-day rolling (default); 14-day for Phase 18+ |
| Measurement method | Count workflow runs on main branch. Exclude billing-blocked repos (Tidalarr, Qobuzarr, AppleMusicarr when Actions minutes are exhausted). Include only real workflow executions. |
| Baseline target | >= 98% |
| Phase 18+ target | >= 98% (Phase 23: >= 99%) |
| Escalation | Drop below 95% triggers immediate root-cause analysis. |

### Test Count

| Field | Value |
|-------|-------|
| Formula | Total passing tests from `dotnet test --filter "State!=Quarantined"` |
| Measurement window | Weekly (Monday) |
| Measurement method | Record passed/skipped/failed counts from Brainarr full suite run. |
| Target | Monotonically increasing (new features add tests; refactors preserve count) |
| Escalation | Test count decrease without corresponding test consolidation requires justification. |

---

## Quality KPIs

### Parity Drift

| Field | Value |
|-------|-------|
| Formula | `(plugins with divergent contract implementations) / (total plugins)` per contract |
| Measurement window | Weekly (Monday) |
| Measurement method | Compare contract implementations across Brainarr, Tidalarr, Qobuzarr, AppleMusicarr. A "divergent" plugin has a local implementation of a contract that should come from Common, or uses a different Common SHA than the others. |
| Target | 0% (all plugins on same Common SHA, no local divergence) |
| Escalation | Any divergence triggers submodule bump PR within 48 hours. |

### Common SHA Alignment

| Field | Value |
|-------|-------|
| Formula | `(plugins on latest Common SHA) / (total plugins)` |
| Measurement window | Weekly (Monday) |
| Measurement method | Compare `ext-common-sha.txt` in each plugin repo against Common main HEAD. |
| Target | 4/4 |
| Escalation | Misalignment persisting > 1 week triggers priority bump. |

### Packaging Closure

| Field | Value |
|-------|-------|
| Formula | `(plugins passing closure check) / (total plugins)` |
| Measurement window | Per PR and weekly audit |
| Measurement method | Run `pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests` in each plugin repo. |
| Target | 4/4 |
| Escalation | Any closure failure blocks merge. |

---

## Performance KPIs

### Triage Latency (p95)

| Field | Value |
|-------|-------|
| Formula | 95th percentile of `(triage end - triage start)` per item |
| Measurement window | Per integration test run |
| Measurement method | Measured in Brainarr integration tests via `Stopwatch` around triage operations. |
| Target | < 500ms per item |
| Escalation | p95 > 1s triggers performance investigation. |

### Simulate Latency (p95)

| Field | Value |
|-------|-------|
| Formula | 95th percentile of gap plan simulate operation duration |
| Measurement window | Per integration test run |
| Measurement method | Measured in Brainarr integration tests. |
| Target | < 2s |
| Escalation | p95 > 5s triggers performance investigation. |

### Performance Parity

| Field | Value |
|-------|-------|
| Formula | `(new path latency) / (old path latency)` |
| Measurement window | Per parity test run |
| Measurement method | JIT-warmed benchmark comparing old and new code paths. Minimum 100 warmup iterations before measurement. |
| Target | Within 10% (ratio < 1.1) |
| Escalation | Ratio > 2.0 triggers optimization work. |

---

## Safety KPIs

### Audit Replay Rate

| Field | Value |
|-------|-------|
| Formula | `(batches with complete replay capability) / (total applied batches)` |
| Measurement window | Per release |
| Measurement method | Integration test: apply a batch, then replay from audit log. Verify the replayed output matches the original. |
| Target | 100% |
| Escalation | Any batch without replay capability blocks release. |

### Rollback Coverage

| Field | Value |
|-------|-------|
| Formula | `(apply actions with tested rollback) / (total apply actions)` |
| Measurement window | Per release |
| Measurement method | Each apply endpoint must have a corresponding rollback test in the integration suite. |
| Target | 100% |
| Escalation | Uncovered apply action blocks merge. |

### Confidence Calibration Error

| Field | Value |
|-------|-------|
| Formula | `abs(predicted accept rate - actual accept rate)` averaged across confidence bands |
| Measurement window | Per calibration test run |
| Measurement method | Compare predicted confidence bands (high/medium/low) against golden fixture accept rates. |
| Baseline target | < 15% |
| Phase 19+ target | < 10% |
| Escalation | Error > 20% triggers recalibration. |

---

## Operational KPIs

### Monotonicity (Gap Planner)

| Field | Value |
|-------|-------|
| Formula | `(identical-input pairs with identical output) / (total pairs)` |
| Measurement window | Per test run |
| Measurement method | Run gap planner twice with same library state; outputs must be identical. |
| Target | 100% |
| Escalation | Any non-determinism is a bug. |

### Budget Enforcement

| Field | Value |
|-------|-------|
| Formula | `(plans exceeding budget that were rejected) / (plans exceeding budget)` |
| Measurement window | Per test run |
| Measurement method | Integration tests submit plans that exceed configured budget limits. |
| Target | 100% |
| Escalation | Budget bypass is a security issue. |

### Idempotency Rejection

| Field | Value |
|-------|-------|
| Formula | `(duplicate apply attempts correctly rejected) / (total duplicates)` |
| Measurement window | Per test run |
| Measurement method | Submit the same apply operation with the same idempotency key twice. Second must be rejected. |
| Target | 100% |
| Escalation | Duplicate acceptance is a correctness bug. |

---

## Evidence Standards

Every KPI measurement must be accompanied by a structured evidence artifact:

```text
KPI: <name>
Formula: <formula with actual values>
Value: <computed result>
Target: <threshold>
Met: Yes/No
Date: <ISO 8601>
Command: <exact command run>
Exit code: <0 or non-zero>
Commit: <short SHA of code under test>
```

For submodule alignment, include:

```text
Submodule State:
  Brainarr:      gitlink=<sha> ext-common-sha.txt=<sha> match=Yes/No
  Tidalarr:      gitlink=<sha> ext-common-sha.txt=<sha> match=Yes/No
  Qobuzarr:      gitlink=<sha> ext-common-sha.txt=<sha> match=Yes/No
  AppleMusicarr: gitlink=<sha> ext-common-sha.txt=<sha> match=Yes/No
```

---

## Measurement Schedule

| Cadence | KPIs | Responsible |
|---------|------|-------------|
| Daily | Flake rate, CI pass rate | Automated or first session |
| Weekly (Monday) | All KPIs | Monday audit session |
| Per PR | Packaging closure, test count | CI or local verification |
| Per release | Audit replay, rollback coverage | Release gate |

---

## Phase-Specific Target Overrides

Phases may tighten (never loosen) baseline targets:

| Phase | KPI | Override Target | Rationale |
|-------|-----|-----------------|-----------|
| 17 | Flake rate | < 1% (7-day) | Baseline establishment |
| 18 | Flake rate | < 0.5% (14-day) | Reliability lock |
| 18 | CI pass rate | >= 98% (14-day) | Sustained reliability |
| 19 | Calibration error | < 10% | Tighter calibration |
| 23 | CI pass rate | >= 99% | Near-perfect CI |
| 23 | Flake rate | < 0.5% (7-day) | Debt burn-down standard |
