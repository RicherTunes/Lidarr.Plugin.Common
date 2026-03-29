# Release & Promotion Policy

## Consumption Model

Common is consumed via **Git submodule** (primary) — NOT NuGet packages.

All 4 plugin repos reference Common as `ext/Lidarr.Plugin.Common` submodule. This is the supported, tested, and promoted path. NuGet packages are published as a convenience for external consumers but are not required for the plugin ecosystem.

### NuGet Status

- Packages are built and attached to GitHub Releases as artifacts
- NuGet.org publishing requires `NUGET_API_KEY` secret on this repository (not yet configured)
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

## NuGet Publishing Procedure

### First-time setup
1. Create NuGet.org account (or use existing)
2. Generate API key:
   - Name: `Lidarr.Plugin.Common GitHub Actions`
   - Expiration: 365 days
   - Glob: `Lidarr.Plugin.*`
   - Scope: Push new packages and package versions
3. Set as **repository-level** secret (Common repo only — plugins consume via submodule, not NuGet):
   ```pwsh
   gh secret set NUGET_API_KEY --repo RicherTunes/Lidarr.Plugin.Common
   ```
4. Verify:
   ```pwsh
   gh secret list --repo RicherTunes/Lidarr.Plugin.Common
   ```

### Manual publish (for existing releases)

**PowerShell:**
```pwsh
$tmp = New-Item -ItemType Directory -Path "$env:TEMP/nuget-publish" -Force
Push-Location $tmp
gh release download v1.7.1 -p "*.nupkg" -R RicherTunes/Lidarr.Plugin.Common
dotnet nuget push "Lidarr.Plugin.Abstractions.1.7.1.nupkg" --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY --skip-duplicate
dotnet nuget push "Lidarr.Plugin.Common.1.7.1.nupkg" --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY --skip-duplicate
Pop-Location
Remove-Item $tmp -Recurse -Force
```

**Bash:**
```bash
mkdir -p /tmp/nuget-publish && cd /tmp/nuget-publish
gh release download v1.7.1 -p "*.nupkg" -R RicherTunes/Lidarr.Plugin.Common
dotnet nuget push "Lidarr.Plugin.Abstractions.1.7.1.nupkg" --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY" --skip-duplicate
dotnet nuget push "Lidarr.Plugin.Common.1.7.1.nupkg" --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY" --skip-duplicate
rm -rf /tmp/nuget-publish
```

### Key rotation
- Keys expire after 365 days
- Rotation: generate new key on NuGet.org, update GitHub secret
- If key holder unavailable: org admin generates new key via NuGet.org API Keys page

### Automated publish (future releases)
Once `NUGET_API_KEY` secret is set, the release workflow automatically publishes on tag push.

## Current Baseline

- Common: v1.7.1 (2026-03-27)
- Host target: pr-plugins-3.1.2.4913 (net8.0)
- All plugins on SHA `aae92da`
