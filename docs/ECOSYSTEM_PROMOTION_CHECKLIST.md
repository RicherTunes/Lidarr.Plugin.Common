# Ecosystem Promotion Checklist

## When to use
Run this checklist before promoting a new Common release to all plugin repos.

## Prerequisites
- Common release tag pushed (e.g., v1.7.1)
- GitHub Release workflow completed successfully

## Per-Plugin Verification Matrix

### For each of: Tidalarr, Qobuzarr, AppleMusicarr

| Check | Command | Expected |
|-------|---------|----------|
| Submodule bump | `cd ext/Lidarr.Plugin.Common && git checkout <tag>` | Clean checkout |
| Build | `dotnet build -m:1` | 0 errors |
| Runtime sandbox | `dotnet test --filter "Category=Runtime" --blame-hang-timeout 30s` | All pass |
| Full test suite | `dotnet test --blame-hang-timeout 30s` | 0 new failures |
| Docker smoke (optional) | `verify-local.ps1 -IncludeSmoke` | Plugin loads in Lidarr |

### For Brainarr (bridge-exempt)

| Check | Command | Expected |
|-------|---------|----------|
| Submodule bump + SHA file | Update `ext-common-sha.txt` | SHA matches |
| Build | `dotnet build -m:1` | 0 errors |
| Runtime sandbox | `dotnet test --filter "Category=Runtime" --blame-hang-timeout 30s` | All pass |
| Full test suite | `dotnet test --blame-hang-timeout 30s` | 0 new failures |

## CI Rules (future — requires billing unblock)
- [ ] Exactly one concrete IPlugin per plugin assembly
- [ ] No new net6.0 references in build files
- [ ] All shipped bridge contracts have: default impl + compliance test + consumer test
- [ ] `.bridge-exempt` repos excluded from bridge parity checks

## Current Baseline
- Common: v1.7.1 (2026-03-27)
- Host target: pr-plugins-3.1.2.4913 (net8.0)
- Runtime tests: 39/39 green across all 4 plugins
