# Ecosystem Handoff Guide

This document enables another developer (human or AI) to continue ecosystem work with full context. It captures the current state, ongoing work, and decision rationale.

## Quick Start for New Contributor

### 1. Workspace Setup

```powershell
# Clone the root workspace (contains all repos)
cd D:\Alex\github

# Repos in this workspace:
# - brainarr/          AI-powered import list plugin
# - qobuzarr/          Qobuz streaming plugin
# - tidalarr/          Tidal streaming plugin
# - applemusicarr/     Apple Music plugin (metadata only)
# - lidarr.plugin.common/  Shared library + tooling

# Each plugin has Common as a submodule:
git -C qobuzarr submodule update --init --recursive
git -C tidalarr submodule update --init --recursive
git -C brainarr submodule update --init --recursive
git -C applemusicarr submodule update --init --recursive
```

### 2. Build & Test

```powershell
# Build all plugins
dotnet build qobuzarr/Qobuzarr.csproj -c Release
dotnet build tidalarr/src/Tidalarr/Tidalarr.csproj -c Release
dotnet build brainarr/Brainarr.Plugin/Brainarr.Plugin.csproj -c Release
dotnet build applemusicarr/src/AppleMusicarr.Plugin/AppleMusicarr.Plugin.csproj -c Release

# Run tests
dotnet test qobuzarr/Qobuzarr.Tests/
dotnet test tidalarr/tests/
dotnet test brainarr/Brainarr.Tests/
```

### 3. Package Plugins

```powershell
# Use the unified PluginPack tooling
Import-Module lidarr.plugin.common/tools/PluginPack.psm1

# Package with canonical Abstractions (recommended)
New-PluginPackage -Csproj qobuzarr/Qobuzarr.csproj -Manifest qobuzarr/plugin.json -RequireCanonicalAbstractions
```

## Current Work Streams

### Active PRs

Check these repos for open PRs:
- `gh pr list -R RicherTunes/Lidarr.Plugin.Common`
- `gh pr list -R RicherTunes/Qobuzarr`
- `gh pr list -R RicherTunes/Tidalarr`
- `gh pr list -R RicherTunes/Brainarr`
- `gh pr list -R RicherTunes/AppleMusicarr`

### Canonical Abstractions (Completed)

All plugins now use `canonical-abstractions.json` to ensure identical Abstractions.dll:
- Version: 1.5.0
- SHA256: `251bf049c28737ac1912074733adf04f099f54c801c914ac9c0e056b2a8232db`

Verification: `lidarr.plugin.common/scripts/Verify-CanonicalAbstractions.ps1`

### Manifest Entrypoint Validation (Completed)

`ManifestCheck.ps1 -ResolveEntryPoints` validates:
- `main` DLL exists in publish output
- Types exist in declared `rootNamespace`
- Plugin interface implementations discoverable

## Architecture Decisions

### Why Canonical Abstractions?

**Problem**: Plugins built at different times had byte-different Abstractions.dll, causing potential type identity issues.

**Solution**: Pin Abstractions to a specific release with SHA256 verification:
1. `canonical-abstractions.json` stores version + SHA256
2. `Install-CanonicalAbstractions` downloads/caches the DLL
3. `New-PluginPackage -RequireCanonicalAbstractions` enforces injection
4. `Assert-CanonicalAbstractions` verifies post-package

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
| `tools/canonical-abstractions.json` | Common | Pinned Abstractions config |
| `scripts/e2e-runner.ps1` | Common | E2E test orchestration |
| `docs/reference/plugin.schema.json` | Common | Manifest JSON schema |
| `plugin.json` | Each plugin | Plugin-specific manifest |
| `build.ps1` | Each plugin | Build script |

## Known Issues

### GitHub Actions Billing

When Actions is blocked by billing limits:
1. Run builds locally (see CLAUDE.md)
2. Verify manually before merging
3. Do NOT trust cached CI status

### Multi-Plugin E2E Instability

The Lidarr host has an AssemblyLoadContext lifecycle bug that affects multi-plugin scenarios.
- Use single-plugin E2E for reliable testing
- `:8691` (multi-plugin) is "best-effort"
- See `ECOSYSTEM_PARITY_ROADMAP.md` for details

## Merge Order (When CI Unblocks)

1. Common PRs first (e.g., canonical Abstractions, entrypoint validation)
2. Bump Common submodule in each plugin
3. Merge plugin PRs (verify locally first)

## Contact

- Repository: https://github.com/RicherTunes/Lidarr.Plugin.Common
- Issues: https://github.com/RicherTunes/Lidarr.Plugin.Common/issues
