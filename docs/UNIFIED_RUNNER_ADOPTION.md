# Unified Test Runner Adoption Guide

This document explains how to migrate CI workflows from raw `dotnet test` calls to the unified test runner.

## Why Use the Unified Runner?

The unified test runner (`scripts/test.ps1`) provides:

1. **Consistent filtering**: Categories (Integration, Packaging, LibraryLinking, Benchmark, Slow) and quarantine state are handled centrally
2. **Quarantine support**: `State=Quarantined` tests are excluded by default, preventing flaky test noise
3. **CI integration**: GitHub Actions annotations, TRX parsing, proper exit codes
4. **Single source of truth**: Filter logic lives in one place, not scattered across workflows

## Migration Steps

### 1. Add the Lint to CI

Add the adoption lint to your CI workflow (typically in the parity-lint job):

```yaml
- name: Lint Test Runner Adoption
  shell: pwsh
  run: |
    $script = "ext/Lidarr.Plugin.Common/scripts/lint-ci-uses-runner.ps1"
    if (Test-Path $script) {
      & $script -Path . -CI
    } else {
      Write-Warning "lint-ci-uses-runner.ps1 not found - skipping"
    }
```

### 2. Create an Allowlist (If Needed)

If you have legitimate exceptions (e.g., mutation testing, test discovery), create `.github/test-runner-allowlist.json`:

```json
{
  "description": "Allowlist for raw dotnet test calls",
  "patterns": [
    {
      "file": "mutation-tests.yml",
      "reason": "Stryker.NET requires direct dotnet test invocation"
    },
    {
      "file": "*.yml",
      "line_pattern": "--list-tests",
      "reason": "Test discovery, not execution"
    },
    {
      "file": "dependency-update.yml",
      "expiresOn": "2025-03-01",
      "owner": "alex",
      "reason": "Temporary exemption during migration - must be fixed by March"
    }
  ]
}
```

**Expiration Fields:**
- `expiresOn`: ISO date (YYYY-MM-DD) after which the exemption is no longer valid
- `owner`: Person responsible for resolving the exemption before expiry

Expired exemptions will cause the lint to fail even in report-only mode.

### 3. Migrate Workflow Steps

Replace raw `dotnet test` calls with the unified runner:

**Before:**
```yaml
- name: Run Tests
  run: |
    dotnet test MyProject.sln \
      --configuration Release \
      --no-build \
      --filter "Category!=Integration&Category!=Slow" \
      --collect "XPlat Code Coverage" \
      --results-directory TestResults
```

**After:**
```yaml
- name: Run Tests
  shell: pwsh
  run: |
    $script = "ext/Lidarr.Plugin.Common/scripts/test.ps1"
    if (Test-Path $script) {
      & $script -Configuration Release -NoBuild -Coverage -OutputDir TestResults -CI
    } else {
      Write-Error "FATAL: Unified test runner not found at $script"
      exit 1
    }
```

## Unified Runner Parameters

| Parameter | Description | Example |
|-----------|-------------|---------|
| `-Configuration` | Build configuration | `-Configuration Release` |
| `-NoBuild` | Skip build step | `-NoBuild` |
| `-Coverage` | Enable XPlat Code Coverage | `-Coverage` |
| `-OutputDir` | Test results directory | `-OutputDir TestResults` |
| `-CI` | Enable CI mode (annotations, strict) | `-CI` |
| `-Category` | Run specific category | `-Category Integration` |
| `-ExcludeCategories` | Override default exclusions | `-ExcludeCategories @()` (runs all) |
| `-IncludeQuarantined` | Include quarantined tests | `-IncludeQuarantined` |
| `-TestProject` | Specific test project | `-TestProject tests/MyTests.csproj` |
| `-AdditionalFilter` | Repo-specific filter expression | `-AdditionalFilter "scope!=cli"` |
| `-Properties` | Additional MSBuild properties | `-Properties @("SkipHostBridge=true")` |

## Common Migration Patterns

### Fast PR Tests (Default)
```powershell
& $script -Configuration Release -NoBuild -CI
# Excludes: Integration, Packaging, LibraryLinking, Benchmark, Slow, Quarantined
```

### Integration Tests
```powershell
& $script -Category Integration -Configuration Release -NoBuild -CI
# Runs only Category=Integration tests
```

### Full Test Suite with Coverage
```powershell
& $script -Configuration Release -NoBuild -Coverage -OutputDir ./coverage -CI
```

### Weekly Quarantine Verification
```powershell
& $script -IncludeQuarantined -Configuration Release -NoBuild -CI
# Includes State=Quarantined tests to verify they still pass
```

### Benchmark/Performance Tests
```powershell
& $script -Category Benchmark -Configuration Release -NoBuild -CI
```

## Adoption Tracking

The `lint-ci-uses-runner.ps1` script tracks adoption:

```bash
# Check violations (report-only)
./scripts/lint-ci-uses-runner.ps1 -Path /path/to/repo

# CI mode (fails on violations)
./scripts/lint-ci-uses-runner.ps1 -Path /path/to/repo -CI

# Show suggested fixes
./scripts/lint-ci-uses-runner.ps1 -Path /path/to/repo -Fix
```

## Current Adoption Status

| Repository | Status | Notes |
|------------|--------|-------|
| Qobuzarr | ✅ Strict | All workflows use unified runner |
| Tidalarr | ✅ Strict | Migrated with `-AdditionalFilter "scope!=cli"` |
| Brainarr | ⚠️ Report-only | Filters updated, workflows not migrated (10 violations) |
| AppleMusicarr | ⚠️ Report-only | Multiple test projects (16 violations) |

## Exceptions That Shouldn't Be Migrated

Some `dotnet test` calls are legitimately not candidates for the unified runner:

1. **Mutation testing** (Stryker.NET): Requires specific environment variables and project targeting
2. **Test discovery** (`--list-tests`): Just enumerates tests, doesn't execute
3. **Registry-specific tests**: May require very targeted filters not supported by the runner

Add these to your allowlist with clear reasons.

## Troubleshooting

### "Unified test runner not found"
Ensure the Common submodule is initialized:
```bash
git submodule update --init --depth=1 -- ext/Lidarr.Plugin.Common
```

### Tests not running
The runner looks for test projects automatically. If it can't find them, specify explicitly:
```powershell
& $script -TestProject tests/MyProject.Tests/MyProject.Tests.csproj -CI
```

### Wrong categories excluded
Check your `-ExcludeCategories` parameter or use `-Category` to run a specific category.
