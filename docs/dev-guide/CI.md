<!-- docval:ignore-workflow-refs docval:ignore-script-refs: references plugin-local verify wrappers as part of the ecosystem contract -->
# Continuous Integration

Gitea is the primary CI surface for the RicherTunes Lidarr plugin ecosystem. GitHub Actions workflows, where a repo still carries them, are mirrors or peripheral automation and are not the assumed merge gate.

## Common CI

Common's Gitea workflow lives at `.gitea/workflows/ci.yml`. It validates the shared library, TestKit, packaging scripts, and ecosystem CI-contract scripts.

The `ecosystem-contract` job is the live five-repo guard. On Common `main` pushes, manual dispatch, and the scheduled run, it clones every active plugin from Gitea and runs:

```powershell
pwsh scripts/ci/verify-ecosystem-ci-contract.ps1 -EcosystemRoot .. -CI
```

That check fails on within-plugin pin drift (`ext-common-sha.txt` vs submodule gitlink), cross-plugin Common SHA divergence, missing Gitea lint/verify wiring, stale GitHub mirror-workflow expectations, unguarded GitHub mirror jobs, fallback lint subsets, and corrupt workflow files. PRs still run the hermetic self-tests for this guard; the live cross-repo check is main/dispatch/schedule scoped because a single Common PR cannot atomically update every plugin pin.

Key local checks:

```powershell
pwsh scripts/ci/verify-ecosystem-ci-contract.ps1 -EcosystemRoot .. -CI
pwsh scripts/lint-doc-script-refs.ps1 -RepoRoot . -CI
pwsh scripts/tests/Test-VersionSourceContract.ps1
```

## Plugin CI Contract

Each active plugin repo should expose two Gitea jobs:

| Job | Required command shape |
|---|---|
| `CI / lint` | `pwsh ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 -RepoPath . -CommonRoot ext/Lidarr.Plugin.Common -Mode ci` |
| `CI / verify` | `pwsh scripts/verify-local.ps1` |

The shared lint runner enforces date parsing, sync-over-async, deterministic test-trait policy, full ecosystem parity (`Structural` + `VersionContract`), doc script/workflow references, and repo-local `scripts/tests/*.ps1` contract tests.

`scripts/verify-local.ps1` delegates to Common's `scripts/local-ci.ps1` for host assembly extraction, build, ILRepack packaging, package-closure checks, and deterministic tests.

## Ecosystem Contract

The active repo list and mirror-workflow expectations live in:

- `scripts/ci/ecosystem-repos.json`
- `scripts/ci/verify-ecosystem-ci-contract.ps1`

Update that manifest when adding a sixth plugin, adding/removing GitHub workflow mirrors, or changing which repos are Gitea-primary. Active plugin mirrors must keep every job guarded with `if: ${{ github.server_url == 'https://github.com' }}` so Gitea remains the primary merge gate without executing duplicate GitHub jobs.

All active plugins must converge on one Common SHA. Temporary cross-repo re-pin windows are allowed while PRs are in flight, but the live Common guard treats a divergent merged ecosystem as a failure because it invalidates the multi-plugin ALC/package proof.

## Releases

Tagged Common releases are still published through release tooling, but plugin PR correctness should be proven by the Gitea jobs and local wrappers above. If a repo relies on GitHub release publishing, document that as release automation, not as primary PR CI.

## Local Verification

Before merging Common changes that affect plugins:

1. Run Common's own tests and script checks.
2. Run `verify-ecosystem-ci-contract.ps1` against the workspace.
3. Re-pin each affected plugin to the intended Common SHA.
4. Run each affected plugin's shared lint runner and `scripts/verify-local.ps1`.
5. Confirm Gitea reports green checks on the pushed branches.

Do not use stale GitHub Actions status as evidence that a Gitea-primary repo is merge-ready.
