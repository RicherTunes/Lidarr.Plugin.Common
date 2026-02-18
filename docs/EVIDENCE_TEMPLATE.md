# Phase Gate Evidence Template

Every phase completion must produce structured evidence artifacts. This template defines the required format so that any AI agent or human reviewer can verify phase gates without ambiguity.

See also: [KPI_DEFINITIONS.md](KPI_DEFINITIONS.md) for measurement formulas and targets.

---

## Required Sections

### 1. Phase Summary

```text
Phase: <number> — <name>
Status: Complete | In Progress | Blocked
Date completed: <ISO 8601>
PR(s): <URLs>
```

### 2. Exit Criteria Checklist

Each exit criterion from the roadmap must be listed with pass/fail and evidence:

```text
Exit Criteria:
  1. [x] <criterion description>
     Evidence: <one-line summary with link or command output>
  2. [x] <criterion description>
     Evidence: ...
  3. [ ] <criterion description>  ← must be empty if phase is not complete
```

### 3. Test Suite Results

```text
Test Suite:
  Command:  dotnet test --filter "State!=Quarantined" --no-restore --verbosity minimal
  Date:     <ISO 8601>
  Commit:   <short SHA>
  Result:   Passed: <n>, Failed: <n>, Skipped: <n>, Total: <n>
  Duration: <time>
  Exit code: 0
```

### 4. KPI Measurements

One row per tracked KPI. Use formulas from [KPI_DEFINITIONS.md](KPI_DEFINITIONS.md):

```text
KPI Measurements:
  | KPI | Formula | Value | Target | Met |
  |-----|---------|-------|--------|-----|
  | Flake rate | failed-then-rerun-pass / total | 0/2416 = 0.00% | < 1% | Yes |
  | CI pass rate | green runs / total runs | 12/12 = 100% | >= 98% | Yes |
  | ... | ... | ... | ... | ... |
```

### 5. Submodule Alignment

```text
Submodule State:
  | Repo | gitlink SHA | ext-common-sha.txt | Match |
  |------|-------------|-------------------|-------|
  | Brainarr | <sha7> | <sha7> | Yes |
  | Tidalarr | <sha7> | <sha7> | Yes |
  | Qobuzarr | <sha7> | <sha7> | Yes |
  | AppleMusicarr | <sha7> | <sha7> | Yes |
```

### 6. CI Evidence

For repos with active CI:

```text
CI Evidence:
  | Workflow | Commit | Result | URL |
  |----------|--------|--------|-----|
  | CI | <sha7> | success | <URL> |
  | Test and Coverage | <sha7> | success | <URL> |
  | ... | ... | ... | ... |
```

### 7. Billing-Blocked Repos

For repos where GitHub Actions is billing-blocked, provide local verification:

```text
Local Verification:
  <repo>:
    cmd:     pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests
    date:    <ISO 8601>
    exit:    0
    summary: BUILD PASS (<time>), PACKAGE PASS, CLOSURE PASS
```

### 8. Redaction Validation

Every phase must confirm no sensitive data in logs or exceptions:

```text
Redaction Check:
  - API keys in logs: None found (grep -r "sk-" --include="*.log" returned 0 results)
  - Credentials in exceptions: None found (test suite includes redaction assertions)
  - Audit trail sanitized: Yes (SensitiveDataMasker covers API keys, JWTs, emails, IPs, CCs)
```

---

## Example: Phase 17 Evidence

```text
Phase: 17 — Tech Debt Burn-Down
Status: In Progress (7-day flake window closes Feb 23)
Date completed: pending
PR(s): https://github.com/RicherTunes/Brainarr/pull/502

Exit Criteria:
  1. [x] EnhancedRecommendationCache registered in DI
     Evidence: BrainarrOrchestratorFactory.ConfigureServices(), 808 lines unconditionally compiled
  2. [x] SecureStructuredLogger registered in DI
     Evidence: BrainarrOrchestratorFactory.ConfigureServices()
  3. [x] ModelRegistryLoader registered in DI
     Evidence: Factory line 47, already wired
  4. [x] All existing tests pass
     Evidence: 2416/2416 pass, 9 skipped, 5 quarantined OOM
  5. [x] Behavior parity tests
     Evidence: 5 roundtrip tests + 10 enhanced cache tests
  6. [ ] 7-day flake metric < 1%
     Evidence: pending (window closes Feb 23)

Test Suite:
  Command:  dotnet test --filter "State!=Quarantined" --no-restore --verbosity minimal
  Date:     2026-02-17T23:57:00Z
  Commit:   fe2f3b6
  Result:   Passed: 2416, Failed: 0, Skipped: 9, Total: 2425
  Duration: 3m 25s
  Exit code: 0

KPI Measurements:
  | KPI | Formula | Value | Target | Met |
  |-----|---------|-------|--------|-----|
  | Flake rate (7-day) | failed-then-rerun-pass / total | 0/2425 = 0.00% | < 1% | Pending (2/7 days) |
  | CI pass rate | green runs / total runs | 100% | >= 98% | Yes |
  | Parity drift | divergent / total | 0/4 = 0% | 0% | Yes |
  | Performance parity | enhanced / basic latency | Within 5x | Within 10% | Yes |

Submodule State:
  | Repo | gitlink SHA | ext-common-sha.txt | Match |
  |------|-------------|-------------------|-------|
  | Brainarr | e46e23b | e46e23b | Yes |
  | Tidalarr | e46e23b | e46e23b | Yes |
  | Qobuzarr | e46e23b | e46e23b | Yes |
  | AppleMusicarr | e46e23b | e46e23b | Yes |
```

---

## Anti-Patterns

- **Screenshots instead of text**: Evidence must be text-based (reproducible). Never use screenshots as primary evidence.
- **"It was green last week"**: Always capture fresh evidence at phase gate time. Code may have drifted.
- **Missing formulas**: Every KPI value must show the formula with actual numerator/denominator, not just the result.
- **Undated evidence**: Every command output must have a timestamp. Undated evidence is not evidence.
- **Partial submodule state**: All 4 plugins must be listed. "Same as last time" is not acceptable.
