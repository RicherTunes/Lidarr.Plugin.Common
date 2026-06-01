# CI and Packaging

Plugin builds are packaged by **PluginPack.psm1** and driven through reusable GitHub Actions workflows. This page points you to the right tools and docs.

## Packaging module

[`tools/PluginPack.psm1`](../tools/PluginPack.psm1) is the PowerShell module that standardises every plugin release. Its main entry point is `New-PluginPackage`, which orchestrates building, manifest validation, host-assembly stripping, and ZIP packaging. Other exported functions — `Get-PluginOutput`, `Test-PluginManifest`, `Invoke-PluginCleanup`, `Invoke-PluginMerge`, `Install-CanonicalAbstractions`, `Assert-CanonicalAbstractions` — can be called individually for advanced scenarios.

Downstream plugins vendor Common as a git submodule, so the module is available to them at `ext/Lidarr.Plugin.Common/tools/PluginPack.psm1`. (The NuGet package itself ships only the library assemblies plus `README.md`/`CHANGELOG.md`, not the `tools/` scripts.)

Full usage details and the recommended folder-based flow → [**Packaging Plugins**](../docs/PACKAGING.md).

## Reusable workflows

Common ships several reusable workflow definitions under `.github/workflows/`:

| Workflow | Purpose |
|---|---|
| `release-plugin.yml` | Build, package, and publish a plugin release |
| `codeql-reusable.yml` | CodeQL security analysis |
| `quarantine-review-reusable.yml` | Quarantine-gate review |
| `multi-plugin-smoke-test.yml` | Multi-plugin integration smoke test |

Plugin repos call these via `uses: ./.github/workflows/<name>.yml` after initialising the Common submodule.

Proposal details and calling conventions → [**CI Reusable Workflows**](../docs/CI_REUSABLE_WORKFLOWS.md).

## SHA-pinning policy

All references to Common workflows **must** use a pinned commit SHA, not a branch tag. Enforcement is automated:

- [`verify-common-pins.yml`](../.github/workflows/verify-common-pins.yml) — CI check that fails on unpinned references.
- [`lint-workflow-sha-pins.ps1`](../scripts/lint-workflow-sha-pins.ps1) — local linter for the same rule.
- [`repin-common-submodule.ps1`](../scripts/repin-common-submodule.ps1) — updates the submodule and rewrites all workflow pins in one step.
- [`ecosystem-pin-drift.yml`](../.github/workflows/ecosystem-pin-drift.yml) — cross-repo monitor ensuring all plugins share the same pinned SHA.

Full inventory of pinned actions and the allowlist mechanism → [**CI SHA Pins**](../docs/CI_SHA_PINS.md).

## CI lane strategy

Workflows are split into fast PR-required lanes and expensive nightly-only lanes to optimise CI billing. The lane definitions and rationale → [**CI Lane Strategy**](../docs/CI_LANE_STRATEGY.md).
