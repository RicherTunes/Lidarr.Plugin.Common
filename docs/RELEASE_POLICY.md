# Release & Promotion Policy

## Consumption Model

Common is consumed via **Git submodule** (primary) — NOT NuGet packages.

All 4 plugin repos reference Common as `ext/Lidarr.Plugin.Common` submodule. This is the supported, tested, and promoted path. NuGet packages are published as a convenience for external consumers but are not required for the plugin ecosystem.

### NuGet Status

- Packages are built and attached to GitHub Releases as artifacts
- NuGet.org publishing requires `NUGET_API_KEY` repository secret (not yet configured)
- Until NuGet is active, external consumers can download `.nupkg` from GitHub Releases

## When to Cut a Release

### Patch Release (x.y.Z)

Required when:

- Bug fixes in bridge defaults or testkit
- Security fixes
- PluginSandbox behavioral changes that affect plugin loading
- Thread safety or concurrency fixes

NOT required for:

- Test-only changes
- Documentation updates
- Internal refactoring with no behavioral change

### Minor Release (x.Y.0)

Required when:

- New bridge contracts shipped (Unshipped to Shipped)
- New default implementations registered in AddBridgeDefaults()
- New testkit fixtures or compliance test infrastructure
- Breaking behavioral changes in existing contracts

### Major Release (X.0.0)

Required when:

- Removing shipped public API types
- Breaking interface signature changes
- Dropping target framework support

## Promotion Workflow

After tagging a Common release:

1. **Release workflow** runs automatically (builds, tests, packs, signs, creates GitHub Release)
2. **Bump submodule** in all 4 plugin repos to the release tag
3. **Run CI** in each plugin repo to verify compatibility
4. **Verify** runtime tests green across all plugins
5. **Update** each plugin's `ext-common-sha.txt` to the release commit

## Submodule Bump Triggers

Plugin repos should bump Common when:

- A new Common release is tagged
- A security fix is merged to Common main
- Bridge contract changes affect the plugin

Plugin repos should NOT bump Common for:

- Test-only changes in Common
- Documentation changes
- Changes that don't affect the public API or testkit

## Current Baseline

- Common: v1.7.1 (2026-03-27)
- Host target: pr-plugins-3.1.2.4913 (net8.0)
- All plugins on SHA `aae92da`
