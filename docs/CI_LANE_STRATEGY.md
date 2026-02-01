# CI Lane Strategy

> **Goal**: Optimize CI billing by separating fast PR-required lanes from expensive nightly-only operations.

This document defines the recommended CI lane structure for the Lidarr plugin ecosystem. The strategy aims to:

1. Keep PR feedback loops fast (< 5 minutes)
2. Minimize billing by deferring expensive Docker operations to nightly runs
3. Maintain comprehensive test coverage through scheduled nightly jobs
4. Provide on-demand triggers for expensive gates when needed

---

## Lane Categories

### PR-Required (Fast, Low Cost)

These lanes run on every PR and push. They must complete quickly to provide rapid feedback.

| Lane | Script/Command | Est. Time | Est. Cost | Description |
|------|----------------|-----------|-----------|-------------|
| **unit-tests** | `scripts/test.ps1` | ~2-3 min | ~$0.02 | Unit tests excluding Integration/Packaging/LibraryLinking/Benchmark/Slow |
| **category-lint** | `scripts/lint-test-categories.ps1 -CI` | ~10 sec | ~$0.001 | Validates test trait categories are approved |
| **build-validate** | `dotnet build + manifest check` | ~1-2 min | ~$0.01 | Build verification and plugin manifest validation |
| **parity-lint** | `scripts/parity-lint.ps1 -Mode ci` | ~30 sec | ~$0.005 | Cross-plugin consistency checks |
| **dotnet-format** | `dotnet format style --verify-no-changes` | ~30 sec | ~$0.005 | Code style verification |

**Total PR Lane Time**: ~4-6 minutes
**Total PR Lane Cost**: ~$0.04/run

#### Test Filter for Fast Lane

The standard exclusion filter for PR unit tests:

```
Category!=Integration&Category!=Packaging&Category!=LibraryLinking&Category!=Benchmark&Category!=Slow
```

This is codified in:
- `scripts/lib/test-runner.psm1` - `Get-StandardTestArgs` function
- Environment variable `CI_TEST_FILTER` in plugin workflows

---

### PR-Optional (On-Demand, Medium Cost)

These lanes are triggered conditionally based on path filters or manual dispatch. They provide targeted validation without running on every commit.

| Lane | Script/Command | Est. Time | Est. Cost | Trigger |
|------|----------------|-----------|-----------|---------|
| **packaging-tests** | `scripts/test.ps1 -Category Packaging,LibraryLinking` | ~3-5 min | ~$0.05 | Path filter (see below) |
| **manifest-gates** | `tools/ManifestCheck.ps1 -ResolveEntryPoints` | ~1-2 min | ~$0.02 | Path filter |

#### Path Filters for Packaging Lane

The packaging lane should trigger when any of these paths change:

```yaml
paths:
  - 'build/**'
  - '*.targets'
  - '**/PluginPackaging.targets'
  - 'src/**/ServiceCollectionExtensions.cs'
  - 'plugin.json*'
  - 'tools/PluginPack.psm1'
  - 'tools/ManifestCheck.ps1'
  - 'Directory.Build.props'
  - 'Directory.Packages.props'
  - '**/AssemblyInfo.cs'
  - '**/*.ilrepack.rsp'
```

#### Example Conditional Trigger

```yaml
packaging-tests:
  name: Packaging Tests (conditional)
  runs-on: ubuntu-latest
  if: |
    github.event_name == 'workflow_dispatch' ||
    contains(github.event.head_commit.modified, 'build/') ||
    contains(github.event.head_commit.modified, '.targets') ||
    contains(github.event.head_commit.modified, 'plugin.json')
```

---

### Nightly-Only (Expensive, Comprehensive)

These lanes run on schedule (typically 2-6 AM UTC) and provide comprehensive validation that would be too slow/expensive for every PR.

| Lane | Script/Command | Est. Time | Est. Cost | Description |
|------|----------------|-----------|-----------|-------------|
| **integration-tests** | `scripts/test.ps1 -Category Integration` | ~5-15 min | ~$0.10-0.20 | External service tests (API calls, network) |
| **slow-tests** | `scripts/test.ps1 -Category Slow` | ~5-10 min | ~$0.08-0.15 | Tests with timing dependencies (>5s runtime) |
| **benchmark-tests** | `scripts/test.ps1 -Category Benchmark` | ~3-5 min | ~$0.05-0.08 | Performance measurement tests |
| **docker-smoke** | `scripts/multi-plugin-docker-smoke-test.ps1` | ~10-20 min | ~$0.15-0.30 | Full Docker container test with plugin loading |
| **quarantine-check** | `scripts/manage-quarantine.ps1 -Mode check` | ~2-3 min | ~$0.03 | Verify quarantined tests still fail as expected |
| **dependency-check** | `dotnet outdated` | ~1-2 min | ~$0.02 | Check for outdated packages |
| **multi-dotnet** | Matrix: .NET 8.0.x, 9.0.x | ~10-15 min | ~$0.10-0.20 | Cross-version compatibility |

**Total Nightly Lane Time**: ~40-70 minutes
**Total Nightly Lane Cost**: ~$0.50-1.00/run

---

## Unified Test Runner Entry Point

### `scripts/test.ps1`

A unified test runner entry point that should exist in Common (for reference) and be copied/adapted by plugins.

```powershell
#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unified test runner for Lidarr plugin ecosystem.

.PARAMETER Category
    Run tests for specific category(s). Multiple categories can be specified.
    Values: Unit, Integration, Packaging, LibraryLinking, Benchmark, Slow

.PARAMETER ExcludeCategories
    Categories to exclude (default for fast lane: Integration,Packaging,LibraryLinking,Benchmark,Slow)

.PARAMETER Coverage
    Enable code coverage collection.

.PARAMETER Configuration
    Build configuration (Debug or Release).

.EXAMPLE
    ./test.ps1
    # Runs fast unit tests (excludes expensive categories)

.EXAMPLE
    ./test.ps1 -Category Integration
    # Runs only Integration tests

.EXAMPLE
    ./test.ps1 -Category Packaging,LibraryLinking -Coverage
    # Runs packaging tests with coverage
#>
param(
    [ValidateSet('Unit', 'Integration', 'Packaging', 'LibraryLinking', 'Benchmark', 'Slow')]
    [string[]]$Category = @(),

    [string[]]$ExcludeCategories = @('Integration', 'Packaging', 'LibraryLinking', 'Benchmark', 'Slow'),

    [switch]$Coverage,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

# Import shared module
$CommonScripts = Join-Path $PSScriptRoot "lib"
Import-Module (Join-Path $CommonScripts "test-runner.psm1") -Force

# Build filter
$filter = ""
if ($Category.Count -gt 0) {
    $filter = ($Category | ForEach-Object { "Category=$_" }) -join "|"
    $ExcludeCategories = @()  # Don't exclude if specific categories requested
}

# Get test arguments
$testArgs = Get-StandardTestArgs `
    -TestProject "*.Tests.csproj" `
    -Configuration $Configuration `
    -OutputDir "TestResults" `
    -Filter $filter `
    -ExcludeCategories $ExcludeCategories `
    -Coverage:$Coverage

# Run tests
& dotnet @testArgs
$exitCode = $LASTEXITCODE

# Parse and display results
$trxFile = Get-ChildItem -Path "TestResults" -Filter "*.trx" -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($trxFile) {
    $summary = Get-TrxTestSummary -TrxPath $trxFile.FullName
    if ($summary) {
        Write-TestSummary -Summary $summary
    }
}

exit $exitCode
```

---

## Workflow Templates

### PR Workflow (Fast)

```yaml
name: CI

on:
  pull_request:
    branches: [main, develop]
  push:
    branches: [main]

jobs:
  fast-tests:
    name: Fast Tests
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Build
        run: dotnet build -c Release

      - name: Unit Tests (fast lane)
        shell: pwsh
        run: ./scripts/test.ps1 -Configuration Release

      - name: Category Lint
        shell: pwsh
        run: ./ext/Lidarr.Plugin.Common/scripts/lint-test-categories.ps1 -CI

  parity-lint:
    name: Parity Lint
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive

      - name: Run Parity Lint
        shell: pwsh
        run: ./ext/Lidarr.Plugin.Common/scripts/parity-lint.ps1 -Mode ci
```

### Nightly Workflow (Comprehensive)

```yaml
name: Nightly

on:
  schedule:
    - cron: '0 5 * * *'  # 5 AM UTC
  workflow_dispatch:
    inputs:
      run_docker_smoke:
        description: 'Run Docker smoke tests'
        type: boolean
        default: true

jobs:
  integration-tests:
    name: Integration Tests
    runs-on: ubuntu-latest
    timeout-minutes: 30
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Build
        run: dotnet build -c Release

      - name: Integration Tests
        shell: pwsh
        run: ./scripts/test.ps1 -Category Integration -Configuration Release

  slow-tests:
    name: Slow Tests
    runs-on: ubuntu-latest
    timeout-minutes: 45
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Build
        run: dotnet build -c Release

      - name: Slow Tests
        shell: pwsh
        run: ./scripts/test.ps1 -Category Slow -Configuration Release

  docker-smoke:
    name: Docker Smoke Test
    runs-on: ubuntu-latest
    timeout-minutes: 30
    if: ${{ github.event_name == 'schedule' || inputs.run_docker_smoke }}
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Build and Package
        run: |
          dotnet build -c Release
          # Package step...

      - name: Docker Smoke Test
        shell: pwsh
        run: |
          ./scripts/multi-plugin-docker-smoke-test.ps1 `
            -LidarrTag "pr-plugins-3.1.1.4884" `
            -PluginZip @("plugin=./artifacts/packages/Plugin.zip")
```

---

## Cost/Time Estimates Summary

| Lane Type | Time | Cost/Run | Frequency | Monthly Cost (30 PRs/day) |
|-----------|------|----------|-----------|---------------------------|
| PR-Required | ~5 min | ~$0.04 | Every PR | ~$36 |
| PR-Optional | ~5 min | ~$0.05 | ~20% of PRs | ~$9 |
| Nightly | ~60 min | ~$0.75 | Daily | ~$23 |
| **Total** | - | - | - | **~$68** |

---

## Migration Checklist

For plugins adopting this strategy:

- [ ] Create `scripts/test.ps1` using template above
- [ ] Update CI workflow to use script instead of inline `dotnet test --filter`
- [ ] Add path filters for packaging lane
- [ ] Move Docker-heavy tests to nightly workflow
- [ ] Verify `CI_TEST_FILTER` env var matches script defaults
- [ ] Add `lint-test-categories.ps1 -CI` to PR workflow
- [ ] Test workflow locally with `act` or similar

---

## Related Documentation

- [Testing with TestKit](./TESTING_WITH_TESTKIT.md) - Test category conventions
- [Multi-Plugin Smoke Test](./MULTI_PLUGIN_SMOKE_TEST.md) - Docker smoke test details
- [Ecosystem Parity Roadmap](./ECOSYSTEM_PARITY_ROADMAP.md) - Cross-plugin consistency

---

## Appendix: Test Categories Reference

| Category | Purpose | When to Use |
|----------|---------|-------------|
| `Integration` | External API/network calls | Tests requiring live services |
| `Packaging` | ILRepack/merged assembly validation | Tests requiring built package |
| `LibraryLinking` | Assembly isolation checks | Tests loading plugin DLLs |
| `Benchmark` | Performance measurement | Timing-sensitive tests |
| `Slow` | Long-running tests (>5s) | Tests with inherent delays |

Categories are validated by `lint-test-categories.ps1` which ensures only approved categories are used across the ecosystem.
