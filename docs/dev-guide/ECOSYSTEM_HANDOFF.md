<!-- docval:ignore-script-refs: references plugin-local verify wrappers from the workspace root -->

# Ecosystem Handoff Guide

This document enables another developer (human or AI) to continue ecosystem work with full context. It captures the current state, ongoing work, and decision rationale.

## Quick Start for New Contributor

### 1. Workspace Setup

```powershell
# Clone the root workspace (contains all repos)
cd D:\Alex\github

# Repos in this workspace:
# - amazonmusicarr/    Amazon Music streaming plugin
# - applemusicarr/     Apple Music import-list/indexer/download-client plugin
# - brainarr/          AI-powered import list plugin
# - qobuzarr/          Qobuz streaming plugin
# - tidalarr/          Tidal streaming plugin
# - lidarr.plugin.common/  Shared library + tooling

# Each plugin has Common as a submodule:
git -C amazonmusicarr submodule update --init --recursive
git -C applemusicarr submodule update --init --recursive
git -C brainarr submodule update --init --recursive
git -C qobuzarr submodule update --init --recursive
git -C tidalarr submodule update --init --recursive
```

### 2. Build & Test

```powershell
# Build all plugins
dotnet build amazonmusicarr/src/Lidarr.Plugin.Amazonmusicarr/Lidarr.Plugin.Amazonmusicarr.csproj -c Release
dotnet build applemusicarr/src/AppleMusicarr.Plugin/AppleMusicarr.Plugin.csproj -c Release
dotnet build brainarr/Brainarr.Plugin/Brainarr.Plugin.csproj -c Release
dotnet build qobuzarr/Qobuzarr.csproj -c Release
dotnet build tidalarr/src/Tidalarr/Tidalarr.csproj -c Release

# Run the same local verify wrappers used by Gitea CI where available
pwsh amazonmusicarr/scripts/verify-local.ps1
pwsh applemusicarr/scripts/verify-local.ps1
pwsh brainarr/scripts/verify-local.ps1
pwsh qobuzarr/scripts/verify-local.ps1
pwsh tidalarr/scripts/verify-local.ps1
```

### 3. Package Plugins

```powershell
# Use the unified PluginPack tooling
Import-Module lidarr.plugin.common/tools/PluginPack.psm1

# Package with merged/internalized Common + Abstractions
New-PluginPackage -Csproj qobuzarr/Qobuzarr.csproj -Manifest qobuzarr/plugin.json
```

## Current Work Streams

### Active PRs

Check these repos for open PRs:
- `gh pr list -R RicherTunes/Lidarr.Plugin.Common`
- `gh pr list -R RicherTunes/amazonmusicarr`
- `gh pr list -R RicherTunes/AppleMusicarr`
- `gh pr list -R RicherTunes/Brainarr`
- `gh pr list -R RicherTunes/Qobuzarr`
- `gh pr list -R RicherTunes/Tidalarr`

### Merged Abstractions Packaging (Completed)

Plugins that import Common's `build/PluginPackaging.targets` merge and internalize
`Lidarr.Plugin.Abstractions.dll` and `Lidarr.Plugin.Common.dll` into the main plugin
DLL. Packages must not ship either DLL as a sidecar.

Legacy sidecar packages are still checked by
`lidarr.plugin.common/scripts/Verify-CanonicalAbstractions.ps1`; when no sidecars
are present, the package is compliant.

### Manifest Entrypoint Validation (Completed)

`ManifestCheck.ps1 -ResolveEntryPoints` validates:
- `main` DLL exists in publish output
- Types exist in declared `rootNamespace`
- Plugin interface implementations discoverable

## Architecture Decisions

### Why Merged Abstractions?

**Problem**: Plugins built at different times had byte-different Abstractions.dll sidecars, causing type identity issues.

**Solution**: Merge/internalize Abstractions and Common into the plugin DLL:
1. `PluginPackaging.targets` includes both DLLs in the ILRepack input.
2. `PluginPack.psm1` removes and rejects Abstractions/Common sidecars.
3. Packaging preflight fails if either sidecar is present.
4. The legacy canonical verifier only compares hashes when old sidecars are present.

### Why Manifest Entrypoint Validation?

**Problem**: Manifests could declare `main` or `rootNamespace` that didn't match actual built assemblies.

**Solution**: `ManifestCheck.ps1 -ResolveEntryPoints` uses System.Reflection.Metadata to:
1. Verify `main` DLL exists
2. Verify types in `rootNamespace` exist
3. Discover plugin types (Indexer/DownloadClient/ImportList)

## File Ownership

| File/Path | Owner | Notes |
|-----------|-------|-------|
| `tools/PluginPack.psm1` | Common | Core packaging logic |
| `tools/ManifestCheck.ps1` | Common | Manifest validation |
| `tools/canonical-abstractions.json` | Common | Legacy sidecar hash config |
| `scripts/e2e-runner.ps1` | Common | E2E test orchestration |
| `docs/reference/plugin.schema.json` | Common | Manifest JSON schema |
| `plugin.json` | Each plugin | Plugin-specific manifest |
| `build.ps1` | Each plugin | Build script |

## Known Issues

### Gitea-Primary CI

Gitea is the primary CI surface. GitHub Actions workflows are mirrors/peripheral where present and must not be treated as the only merge gate. When the Gitea runner is unavailable, run each plugin's `scripts/verify-local.ps1` and the shared plugin lint runner locally before merging.

### Multi-Plugin E2E

The current packaging contract merges/internalizes Common and Abstractions into each plugin DLL to avoid cross-plugin AssemblyLoadContext type-identity conflicts. Use single-plugin Docker E2E for plugin-specific smoke coverage and the Common multi-plugin coexistence proof when changing package closure, host-pinned dependencies, or Common internalization.

## Merge Order (When CI Unblocks)

1. Common PRs first (e.g., packaging policy, entrypoint validation)
2. Bump Common submodule in each plugin
3. Merge plugin PRs (verify locally first)

## Contact

- Repository: [Lidarr.Plugin.Common](https://github.com/RicherTunes/Lidarr.Plugin.Common)
- Issues: [Lidarr.Plugin.Common Issues](https://github.com/RicherTunes/Lidarr.Plugin.Common/issues)
