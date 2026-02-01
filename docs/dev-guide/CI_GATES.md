# CI Gates (Plugin Repos)

This ecosystem treats packaging and parity checks as **non-negotiable gates**. The recommended implementation is a reusable workflow hosted in `lidarr.plugin.common` so every plugin repo runs the same logic.

## Recommended: reusable workflow call

Add a job to your plugin repo workflow (recommended filename: `.github/workflows/packaging-gates.yml`):

```yaml
name: Packaging Gates

on:
  pull_request:
  push:
    branches: [ main ]

jobs:
  packaging-gates:
    uses: RicherTunes/lidarr.plugin.common/.github/workflows/packaging-gates.yml@main
    with:
      common-path: ext/lidarr.plugin.common
      plugin-csproj: src/Your.Plugin/Your.Plugin.csproj
      manifest-path: src/Your.Plugin/plugin.json
      # Optional overrides:
      # framework: net8.0
      # configuration: Release
      # man004-cutoff: "2026-03-01"
    secrets:
      # Optional (only needed if submodules are private in your org):
      # submodules-token: ${{ secrets.SUBMODULES_TOKEN }}
      submodules-token: ${{ secrets.GITHUB_TOKEN }}
```

## What the gate enforces

- **Packaging** via `tools/PluginPack.psm1` (`New-PluginPackage`)
- **Manifest validation** + `-ResolveEntryPoints` (includes `MAN002`/`MAN003` errors)
- **Legacy key warnings** (`MAN004`) escalated from warning → error after `man004-cutoff`
- **Canonical Abstractions**: package must contain the pinned `Lidarr.Plugin.Abstractions.dll` bytes (`tools/canonical-abstractions.json`)
- **Parity lint** (optional): blocks reintroducing known clones and forbidden patterns

## Local equivalents (pre-flight)

From a plugin repo root (with Common submodule available):

```powershell
# Package with canonical Abstractions + entrypoint validation
./build.ps1 Release -Package
```

If you want to run the gates explicitly:

```powershell
$common = "ext/lidarr.plugin.common"

# Manifest validation (includes -ResolveEntryPoints)
& "$common/tools/ManifestCheck.ps1" -ProjectPath "src/Your.Plugin/Your.Plugin.csproj" -ManifestPath "src/Your.Plugin/plugin.json" -PublishPath "src/Your.Plugin/artifacts/publish/net8.0/Release" -ResolveEntryPoints

# Canonical Abstractions check
$expected = (Get-Content "$common/tools/canonical-abstractions.json" -Raw | ConvertFrom-Json).abstractionsSha256
& "$common/scripts/Verify-CanonicalAbstractions.ps1" -PackagePaths @("src/Your.Plugin/artifacts/packages/*.zip") -ExpectedSha256 $expected

# Parity lint (strict CI mode)
& "$common/scripts/parity-lint.ps1" -RepoPath . -Mode ci
```

## Fallback: inline (non-reusable) job steps

If you can’t call the reusable workflow (forks or debugging), the equivalent CI commands are the same as the local pre-flight commands above:

- Package: `./build.ps1 Release -Package`
- Manifest: `tools/ManifestCheck.ps1 ... -ResolveEntryPoints`
- Canonical Abstractions: `scripts/Verify-CanonicalAbstractions.ps1 ...`
- Parity lint: `scripts/parity-lint.ps1 -Mode ci`
