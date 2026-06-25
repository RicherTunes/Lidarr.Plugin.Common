# Ecosystem Promotion Checklist

## When to use
Run this checklist before promoting a new Common release to all plugin repos.

## Prerequisites
- Common release tag pushed (e.g., v1.18.0)
- GitHub Release workflow completed successfully
- Plugin repos have a clean checkout before repinning; do not promote from a dirty
  `ext/Lidarr.Plugin.Common` working tree.

## Per-Plugin Verification Matrix

### For each of: Tidalarr, Qobuzarr, AppleMusicarr

| Check | Command | Expected |
|-------|---------|----------|
| Submodule bump + sentinel | `bash ext/Lidarr.Plugin.Common/scripts/repin-common-submodule.sh <SHA> --stage --verify --path ext/Lidarr.Plugin.Common` | `ext-common-sha.txt` matches gitlink |
| Build | `dotnet build -m:1` | 0 errors |
| Runtime sandbox | `dotnet test --filter "Category=Runtime" --blame-hang-timeout 30s` | All pass |
| Full test suite | `dotnet test --blame-hang-timeout 30s` | 0 new failures |
| Docker smoke (optional) | `verify-local.ps1 -IncludeSmoke` | Plugin loads in Lidarr |

### For Brainarr (bridge-exempt)

| Check | Command | Expected |
|-------|---------|----------|
| Submodule bump + sentinel | `bash ext/Lidarr.Plugin.Common/scripts/repin-common-submodule.sh <SHA> --stage --verify --path ext/Lidarr.Plugin.Common` | `ext-common-sha.txt` matches gitlink |
| Build | `dotnet build -m:1` | 0 errors |
| Runtime sandbox | `dotnet test --filter "Category=Runtime" --blame-hang-timeout 30s` | All pass |
| Full test suite | `dotnet test --blame-hang-timeout 30s` | 0 new failures |

## Required Pin Guards

Every plugin repo must keep both layers enabled:

- `.github/workflows/submodule-pin.yml` for a focused GitHub pin check.
- A `Common submodule pin guard` step in both `.github/workflows/ci.yml` and
  `.gitea/workflows/ci.yml`, running
  `bash ext/Lidarr.Plugin.Common/scripts/repin-common-submodule.sh --verify-only --path ext/Lidarr.Plugin.Common`.

These guards fail when the submodule gitlink, `ext-common-sha.txt`, or the
checked-out submodule state drift from one another.

## CI Rules (future)
- [ ] Exactly one concrete IPlugin per plugin assembly
- [ ] No net6.0 references in build files (net6 retired)
- [ ] All shipped bridge contracts have: default impl + compliance test + consumer test
- [ ] `.bridge-exempt` repos excluded from bridge parity checks

## Current Baseline
- Common: 1.18.0-dev
- Host target: nightly-3.1.3.4970 (net8.0)
- Runtime tests: 39/39 green across all 4 plugins
