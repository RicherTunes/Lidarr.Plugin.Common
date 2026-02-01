# Test Quarantine Process

This document describes the quarantine system for managing flaky or temporarily broken tests
across the plugin ecosystem.

## Overview

The quarantine system uses xUnit's `[Trait("State", "Quarantined")]` attribute to mark tests
that should be temporarily excluded from normal CI runs. This is separate from behavioral
categories like `[Trait("Category", "Integration")]` to avoid polluting test classification.

Key principles:

- **State vs Category**: `State` traits describe test health, `Category` traits describe test behavior
- **Temporary by design**: Quarantined tests should be fixed or removed, not left indefinitely
- **Weekly review**: Quarantined tests run weekly to detect when they've been fixed
- **Documentation required**: Every quarantine must include date, reason, and ideally an issue reference

## When to Quarantine a Test

Quarantine is appropriate when a test:

1. **Is flaky on CI** - Passes locally but fails intermittently on CI runners
2. **Has timing dependencies** - Relies on specific timing that varies by environment
3. **Depends on external services** - Temporarily unavailable service breaks the test
4. **Is temporarily broken** - Known issue being worked on, but blocking other PRs
5. **Has environment-specific issues** - Works on one OS but not another

Quarantine is **NOT** appropriate when:

- The test is simply slow (use `[Trait("Category", "Slow")]` instead)
- The test needs refactoring (fix it or delete it)
- The feature is deprecated (delete the test)
- You don't understand why it fails (investigate first)

## How to Quarantine a Test

### Step 1: Add the State Trait

Add `[Trait("State", "Quarantined")]` to the test method:

```csharp
[Fact]
[Trait("State", "Quarantined")]  // Quarantined 2024-01-27: Flaky on CI due to timing - Issue #123
[Trait("Category", "Integration")]
public async Task SomeFlaky_Test()
{
    // test code
}
```

### Step 2: Add a Comment with Metadata

The comment should include:

- **Date**: When the test was quarantined (YYYY-MM-DD format)
- **Reason**: Brief description of why it's quarantined
- **Issue reference**: Link to the tracking issue (if applicable)

```csharp
// Good examples:
[Trait("State", "Quarantined")]  // Quarantined 2024-01-27: Race condition in concurrent test - Issue #456

[Trait("State", "Quarantined")]  // Quarantined 2024-01-27: External API rate limiting on CI

[Trait("State", "Quarantined")]  // Quarantined 2024-01-27: Intermittent timeout on Windows runners
```

### Step 3: Create a Tracking Issue

Create a GitHub issue to track the quarantined test:

- Title: `[Quarantine] TestClassName.MethodName - Brief reason`
- Label: `test-quarantine`
- Description: Full details about the failure, reproduction steps, investigation notes

### Step 4: Verify Quarantine is Working

Run the quarantine management script to verify your test is detected:

```powershell
./scripts/manage-quarantine.ps1 -Mode report
```

## How to Un-Quarantine a Test

When a quarantined test has been fixed:

### Step 1: Verify the Fix

Run the test multiple times to ensure it's stable:

```bash
# Run the specific test 10 times
for i in {1..10}; do dotnet test --filter "FullyQualifiedName~TestName"; done
```

### Step 2: Remove the Trait

Remove the `[Trait("State", "Quarantined")]` attribute and comment:

```csharp
// Before:
[Fact]
[Trait("State", "Quarantined")]  // Quarantined 2024-01-27: Flaky on CI
[Trait("Category", "Integration")]
public async Task SomeFlaky_Test() { ... }

// After:
[Fact]
[Trait("Category", "Integration")]
public async Task SomeFlaky_Test() { ... }
```

### Step 3: Close the Tracking Issue

Close the associated GitHub issue with a note about the fix.

### Step 4: Verify in CI

Ensure the test passes in CI before merging.

## CI Integration

### Excluding Quarantined Tests

Normal CI runs should exclude quarantined tests:

```yaml
- name: Test (excluding quarantined)
  run: dotnet test --filter "State!=Quarantined"
```

### Weekly Quarantine Check

A weekly workflow runs quarantined tests to detect fixes:

```yaml
name: Weekly Quarantine Check

on:
  schedule:
    - cron: '0 6 * * 0'  # Sunday 6 AM UTC

jobs:
  check-quarantine:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Report quarantined tests
        shell: pwsh
        run: ./scripts/manage-quarantine.ps1 -Mode report -CI

      - name: Run quarantined tests
        run: dotnet test --filter "State=Quarantined" --logger "trx"
        continue-on-error: true

      - name: Analyze results
        shell: pwsh
        run: |
          # Parse .trx for passing tests - these should be un-quarantined
          # Create issues or notifications for tests ready to un-quarantine
```

## Management Script

The `manage-quarantine.ps1` script provides quarantine management capabilities:

### Summary (Default)

```powershell
./scripts/manage-quarantine.ps1
# or
./scripts/manage-quarantine.ps1 -Mode summary
```

Shows a brief overview of quarantine status.

### Full Report

```powershell
./scripts/manage-quarantine.ps1 -Mode report
```

Lists all quarantined tests with locations, dates, and reasons.

### JSON Output

```powershell
./scripts/manage-quarantine.ps1 -Mode report -OutputFormat json
```

Outputs machine-readable JSON for CI integration.

### Markdown Output

```powershell
./scripts/manage-quarantine.ps1 -Mode report -OutputFormat markdown
```

Generates a markdown table for documentation or PR comments.

### Run Quarantined Tests

```powershell
./scripts/manage-quarantine.ps1 -Mode run
```

Outputs the xUnit filter string to run only quarantined tests.

### CI Mode

```powershell
./scripts/manage-quarantine.ps1 -Mode report -CI -MaxQuarantined 10
```

Fails if quarantine count exceeds threshold (default: 20).

## Best Practices

### Do

- Quarantine tests promptly when they block CI
- Always include date and reason in comments
- Create tracking issues for quarantined tests
- Review quarantined tests weekly
- Un-quarantine tests as soon as they're fixed

### Don't

- Leave tests quarantined indefinitely (30+ days is a warning sign)
- Quarantine tests without investigating the root cause
- Use quarantine as a way to skip writing proper tests
- Forget to remove quarantine when the underlying issue is fixed

## Quarantine Limits

To prevent quarantine abuse, CI enforces limits:

- **Default maximum**: 20 quarantined tests per repository
- **Warning threshold**: Tests quarantined for more than 30 days
- **CI failure**: Exceeding the maximum triggers CI failure

These limits encourage teams to fix flaky tests rather than accumulate technical debt.

## Metrics and Monitoring

Track quarantine health over time:

- Number of tests currently quarantined
- Average time tests spend quarantined
- Trend of quarantine count (increasing = problem)
- Number of tests un-quarantined each week

Use the JSON output format for CI integration with monitoring tools:

```powershell
./scripts/manage-quarantine.ps1 -Mode report -OutputFormat json > quarantine-report.json
```

## Relationship to Test Categories

The quarantine system is orthogonal to test categories:

| Trait | Purpose | Example |
|-------|---------|---------|
| `Category=Unit` | Behavioral classification | Fast, isolated tests |
| `Category=Integration` | Behavioral classification | External service tests |
| `Category=Slow` | Performance classification | Tests taking >5s |
| `State=Quarantined` | Health status | Temporarily broken/flaky |

A test can have both a category and a state:

```csharp
[Fact]
[Trait("Category", "Integration")]  // What kind of test it is
[Trait("State", "Quarantined")]     // Current health status
public async Task FlakeyIntegrationTest() { ... }
```
