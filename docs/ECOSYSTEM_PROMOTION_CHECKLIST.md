<!-- docval:ignore-workflow-refs docval:ignore-script-refs: references plugin-local verify wrappers and CI paths as part of the ecosystem promotion contract -->
# Ecosystem Promotion Checklist

## When to use
Run this checklist before promoting a new Common release to all plugin repos.

## Prerequisites
- Common release tag pushed (e.g., v1.18.0)
- Common release workflow completed successfully on its authoritative CI surface
- Plugin repos have a clean checkout before repinning; do not promote from a dirty
  `ext/Lidarr.Plugin.Common` working tree.
- The live ecosystem contract is green:
  `pwsh ./scripts/ci/verify-ecosystem-ci-contract.ps1 -EcosystemRoot <siblings> -CI`.

## Per-Plugin Verification Matrix

### For each streaming plugin: Tidalarr, Qobuzarr, AppleMusicarr, AmazonMusicarr

| Check | Command | Expected |
|-------|---------|----------|
| Submodule bump + sentinel | `bash ext/Lidarr.Plugin.Common/scripts/repin-common-submodule.sh <SHA> --stage --verify --path ext/Lidarr.Plugin.Common` | `ext-common-sha.txt` matches gitlink |
| Shared lint gates | `pwsh ./ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 -RepoPath . -CommonRoot ext/Lidarr.Plugin.Common -Mode ci` | 0 failures |
| Full local verifier | `pwsh ./scripts/verify-local.ps1` | Build/package/tests pass |
| Docker smoke (optional) | `pwsh ./scripts/verify-local.ps1 -IncludeSmoke` | Plugin loads in Lidarr |

### For Brainarr (bridge-exempt)

| Check | Command | Expected |
|-------|---------|----------|
| Submodule bump + sentinel | `bash ext/Lidarr.Plugin.Common/scripts/repin-common-submodule.sh <SHA> --stage --verify --path ext/Lidarr.Plugin.Common` | `ext-common-sha.txt` matches gitlink |
| Shared lint gates | `pwsh ./ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 -RepoPath . -CommonRoot ext/Lidarr.Plugin.Common -Mode ci` | 0 failures |
| Full local verifier | `pwsh ./scripts/verify-local.ps1` | Build/package/tests pass |

## Required Pin Guards

Every plugin repo must keep the Gitea-primary guard enabled:

- A `Common submodule pin guard` step in `.gitea/workflows/ci.yml`, running
  `bash ext/Lidarr.Plugin.Common/scripts/repin-common-submodule.sh --verify-only --path ext/Lidarr.Plugin.Common`.
- Plugin repos carry exactly one guarded GitHub CI mirror at
  `.github/workflows/ci.yml`. The Common ecosystem manifest enforces
  `mirrorWorkflows: 1` for every active plugin, and the mirror contract requires
  `if: ${{ github.server_url == 'https://github.com' }}` on every mirror job.

These guards fail when the submodule gitlink, `ext-common-sha.txt`, or the
checked-out submodule state drift from one another.

If a future plugin needs additional GitHub workflows, first update
`scripts/ci/ecosystem-repos.json` and the ecosystem CI contract so Gitea and
GitHub expectations cannot diverge silently.

## CI Rules (future)
- [ ] Exactly one concrete IPlugin per plugin assembly
- [ ] No net6.0 references in build files (net6 retired)
- [ ] All shipped bridge contracts have: default impl + compliance test + consumer test
- [ ] `.bridge-exempt` repos excluded from bridge parity checks

## Current Baseline
- Common: 1.18.0-dev
- Host target: nightly-3.1.3.4970 (net8.0)
- Active plugin roots: amazonmusicarr, applemusicarr, brainarr, qobuzarr, tidalarr
- CI contract: Gitea primary, exactly one guarded GitHub CI mirror per plugin
