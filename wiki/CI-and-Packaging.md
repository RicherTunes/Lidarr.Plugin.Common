<!-- docval:ignore-script-refs: references plugin-local verify wrappers as part of the ecosystem contract -->

# CI and Packaging

Plugin builds are packaged by **PluginPack.psm1** and validated by a Gitea-primary CI contract. GitHub Actions workflows, where a plugin still carries them, are mirror/peripheral automation and are not the merge gate.

## Packaging module

[`tools/PluginPack.psm1`](../tools/PluginPack.psm1) is the PowerShell module that standardises every plugin release. Its main entry point is `New-PluginPackage`, which orchestrates building, manifest validation, host-assembly stripping, and ZIP packaging. Other exported functions — `Get-PluginOutput`, `Test-PluginManifest`, `Invoke-PluginCleanup`, `Invoke-PluginMerge`, `Install-CanonicalAbstractions`, `Assert-CanonicalAbstractions` — can be called individually for advanced scenarios.

Downstream plugins vendor Common as a git submodule, so the module is available to them at `ext/Lidarr.Plugin.Common/tools/PluginPack.psm1`. (The NuGet package itself ships only the library assemblies plus `README.md`/`CHANGELOG.md`, not the `tools/` scripts.)

Full usage details and the recommended folder-based flow → [**Packaging Plugins**](../docs/PACKAGING.md).

## Shared CI contract

Every active plugin repo should expose the same two Gitea jobs in `.gitea/workflows/ci.yml`:

| Job | Required shape |
|---|---|
| `CI / lint` | Calls `ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 -RepoPath . -CommonRoot ext/Lidarr.Plugin.Common -Mode ci`. |
| `CI / verify` | Calls the plugin's `scripts/verify-local.ps1`, which delegates to Common's `scripts/local-ci.ps1` for host extraction, build, packaging closure, and deterministic tests. |

The ecosystem-level contract is checked by [`scripts/ci/verify-ecosystem-ci-contract.ps1`](../scripts/ci/verify-ecosystem-ci-contract.ps1) against [`scripts/ci/ecosystem-repos.json`](../scripts/ci/ecosystem-repos.json). That manifest is the current source of truth for which plugin repos are Gitea-primary and whether any GitHub workflow mirrors are expected.

The shared lint runner calls `ecosystem-parity-lint.ps1 -Check all`, so plugins get both structural parity checks and version-contract checks through one required CI command.

Common still carries older reusable GitHub workflow definitions and documentation for repos that choose to mirror or manually publish through GitHub, but plugin PR gating should not depend on those workflows unless a repo explicitly restores and verifies them.

## Pinning policy

The mandatory pin is the Common submodule itself:

- plugin gitlink: `ext/Lidarr.Plugin.Common`
- sentinel file: `ext-common-sha.txt`
- manifest version: `commonVersion` in `plugin.json` / `manifest.json`

The submodule gitlink and sentinel must match exactly. Re-pin with [`scripts/repin-common-submodule.ps1`](../scripts/repin-common-submodule.ps1) or [`scripts/repin-common-submodule.sh`](../scripts/repin-common-submodule.sh), then run the plugin lint gate and local verify wrapper.

Workflow SHA pinning only applies to repos that still carry GitHub workflow mirrors that call Common-owned reusable workflows. Repos with no `.github/workflows/*.yml` have no workflow pins to validate.

## CI lane strategy

Gitea is the primary CI target for plugin PRs. Plugin verify jobs should call the repo-local verify-local wrapper, which delegates to Common's `scripts/local-ci.ps1` for host extraction, build, packaging closure, and the deterministic test sweep.

The deterministic test sweep is defined in Common by `scripts/lib/test-trait-policy.psm1`. It always excludes `State=Quarantined`, explicitly keeps `Area=E2E/Hermetic`, and excludes opt-in lanes that need live services, Docker, release artifacts, runtime sandbox host resolution, or benchmark timing. The companion gate `scripts/lint-test-traits.ps1 -CI` validates the CI lane trait vocabulary so new opt-in lanes cannot silently drift away from CI.

Workflows are split into PR-required deterministic lanes and expensive opt-in lanes to keep the self-hosted runner predictable. The lane definitions and rationale → [**CI Lane Strategy**](../docs/CI_LANE_STRATEGY.md).
