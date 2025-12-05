# Investigation: Plugin Loading Issue in Screenshot Workflows

**Date**: 2025-12-05
**Status**: ✅ RESOLVED - Root cause identified and fixed
**Affected Repos**: Tidalarr, Qobuzarr, Brainarr

---

## 🚨 ROOT CAUSE IDENTIFIED 🚨

**The issue was WRONG DIRECTORY STRUCTURE, not TFM or assembly compatibility.**

### The Problem

All workflows were mounting plugins with **flat structure**:
```yaml
-v "plugin-dist:/config/plugins/Tidalarr:ro"  # WRONG!
```

But Lidarr expects **nested structure with owner directory**:
```yaml
-v "plugin-dist:/config/plugins/RicherTunes/Tidalarr:ro"  # CORRECT!
```

### Evidence from Lidarr Source Code

Found in `Tubifarry/Submodules/Lidarr/src/NzbDrone.Common/Extensions/PathExtensions.cs:323-335`:

```csharp
public static List<string> GetPluginAssemblies(this IAppFolderInfo appFolderInfo)
{
    var pluginFolder = appFolderInfo.GetPluginPath();

    if (!Directory.Exists(pluginFolder))
    {
        return new List<string>();
    }

    return Directory.GetDirectories(pluginFolder)
        .SelectMany(owner => Directory.GetDirectories(owner)
            .SelectMany(folder => Directory.GetFiles(folder, "Lidarr.Plugin.*.dll").ToList()))
        .ToList();
}
```

This code:
1. Gets directories in `/config/plugins/` (owner directories)
2. Gets subdirectories within each owner (plugin directories)
3. Finds `Lidarr.Plugin.*.dll` files within those

**With flat structure**: Step 2 returns empty because the plugin folder has no subdirectories (only DLL files)!

### Resolution

Updated all affected workflows:
- `tidalarr/.github/workflows/screenshots.yml`
- `qobuzarr/.github/workflows/screenshots.yml`
- `brainarr/.github/workflows/screenshots.yml`
- `Lidarr.Plugin.Common/.github/workflows/multi-plugin-smoke-test.yml`

Changed mount paths from flat to nested structure with `RicherTunes` as owner.

### Secondary Fix: TFM

Also updated TFM from `net6.0` to `net8.0` to match:
- Tubifarry (working plugin) uses `net8.0`
- Lidarr's `PluginService.cs:77` explicitly looks for `net8.0.zip` packages

---

## Executive Summary

The screenshot workflows are producing incorrect screenshots showing "Couldn't find any results for 'PluginName'" instead of actual plugin configuration dialogs. After fixing the snap.mjs script and mount path issues, the **root cause is now confirmed**: Lidarr is not loading/detecting the plugins at all.

## What Was Fixed

### 1. snap.mjs Card Detection (PRs #97, #98, #99 in Lidarr.Plugin.Common)
- **Issue**: `li:has-text("PluginName")` selector was matching global search autocomplete instead of modal plugin cards
- **Fix**: Removed problematic selectors, added scoped modal detection, text-based fallback
- **Status**: MERGED - Script logic is now correct

### 2. Plugin Mount Path (PRs in each plugin repo)
- **Issue**: Plugins mounted to `/config/plugins/RicherTunes/PluginName` (nested)
- **Fix**: Changed to `/config/plugins/PluginName` (flat structure)
- **Evidence**: Found in `multi-plugin-smoke-test.yml` comment: "Lidarr plugins branch expects flat structure"
- **Status**: MERGED to all three repos
  - Tidalarr: PR #52
  - Qobuzarr: PR #77
  - Brainarr: PR #325

## Current Problem: Plugin Not Loading

### Evidence

**1. Lidarr Container Logs Show NO Plugin Activity**
```
[Info] Bootstrap: Starting Lidarr - /app/bin/Lidarr - Version 2.14.2.4786
[Info] AppFolderInfo: Data directory is being overridden to [/config]
[Info] Microsoft.Hosting.Lifetime: Now listening on: http://[::]:8686
[Info] Microsoft.Hosting.Lifetime: Application started.
```
- Zero plugin scanning messages
- Zero plugin loading messages
- Zero plugin errors
- Lidarr behaves as if plugins don't exist

**2. Plugin API Returns Empty**
```bash
# From workflow logs:
curl -fsS -H "X-Api-Key: $APIKEY" http://localhost:8686/api/v1/system/plugins | grep -qi Tidalarr
# Result: "Tidalarr plugin not detected from Lidarr API after waiting"
```

**3. Plugin Files ARE Being Staged Correctly**
```
=== Staged plugin files ===
-rw-r--r-- 1 runner runner     858 Dec  5 13:40 plugin.json
-rw-r--r-- 1 runner runner  241152 Dec  5 13:40 Lidarr.Plugin.Tidalarr.dll
-rw-r--r-- 1 runner runner  537088 Dec  5 13:40 Lidarr.Plugin.Common.dll
-rw-r--r-- 1 runner runner   53248 Dec  5 13:40 Lidarr.Plugin.Abstractions.dll
... (40+ dependency DLLs)
```

**4. Screenshots Show Wrong Content**
- `indexer-config.png` and `indexer-add-modal.png` are IDENTICAL (same file size)
- Both show the Indexers settings page with global search autocomplete open
- No plugin cards visible because plugin isn't registered with Lidarr

## Hypotheses

### H1: Assembly/TFM Compatibility Issue (MOST LIKELY)
The plugins branch has different base class signatures than release branch:

**Plugins Branch** (what container runs):
```csharp
public abstract string Protocol { get; }  // STRING type
public interface IDownloadProtocol { }    // Interface EXISTS
```

**Release Branch** (what we might be building against):
```csharp
public abstract DownloadProtocol Protocol { get; }  // ENUM type
// IDownloadProtocol interface doesn't exist
```

**Evidence supporting this**:
- Workflow builds with `-p:TargetFramework=net6.0`
- Container is `ghcr.io/hotio/lidarr:pr-plugins-2.14.2.4786`
- CLAUDE.md in Qobuzarr has extensive documentation about this exact issue
- Working plugins (TrevTV's, TypNull's) use specific patterns for plugins branch

### H2: Plugin Discovery Mechanism Not Triggered
Lidarr might require:
- Specific file structure beyond just `/config/plugins/PluginName/`
- Plugin registration via API rather than file-based discovery
- Restart after plugin files are in place (workflow does restart, but timing?)
- Write access to plugins directory (currently mounted as `:ro`)

### H3: Missing Required Files
Plugin might need:
- `manifest.json` in addition to `plugin.json`
- Specific naming conventions
- Entry point configuration

### H4: Silent Load Failure
Plugin DLL might be:
- Failing to load due to missing dependencies
- Throwing exception during initialization
- Incompatible with container's .NET runtime

## Technical Details

### Current Workflow Configuration (screenshots.yml)

```yaml
env:
  DOTNET_VERSION: '8.0.x'
  LIDARR_DOCKER_VERSION: 'pr-plugins-2.14.2.4786'

# Build step
dotnet restore src/Tidalarr/Tidalarr.csproj -p:TargetFramework=net6.0
dotnet build src/Tidalarr/Tidalarr.csproj \
  --configuration Release \
  -p:TargetFramework=net6.0 \
  -p:UsePluginsBranch=true \
  -p:PluginPackagingDisable=true

# Mount step
docker run -d --name lidarr-ss \
  -v "${{ github.workspace }}/plugin-dist:/config/plugins/Tidalarr:ro" \
  ghcr.io/hotio/lidarr:pr-plugins-2.14.2.4786
```

### plugin.json Content (Tidalarr)
```json
{
  "id": "tidalarr",
  "name": "Tidalarr",
  "version": "1.0.1",
  "apiVersion": "1.x",
  "commonVersion": "1.3.0",
  "minHostVersion": "2.14.2.4786",
  "minimumVersion": "2.14.2.4786",
  "entryAssembly": "Lidarr.Plugin.Tidalarr.dll",
  "description": "Tidal integration for Lidarr",
  "author": "RicherTunes"
}
```

### multi-plugin-smoke-test.yml Build (for comparison)
```yaml
# This workflow builds with net8.0, NOT net6.0!
dotnet build src/Tidalarr/Tidalarr.csproj \
  --configuration Release \
  --framework net8.0 \
  -p:PluginPackagingDisable=true
```

## Key Questions to Answer

1. **What TFM does the plugins branch container actually use?**
   - Container comment says ".NET 6.0 runtime"
   - But multi-plugin-smoke-test uses net8.0
   - Which is correct?

2. **What does Lidarr log when it successfully loads a plugin?**
   - Need to find working plugin example logs
   - Compare with our logs (which show nothing)

3. **Does the plugin.json format match what Lidarr expects?**
   - Check Lidarr plugins branch source for plugin discovery code
   - Verify required fields

4. **Is the `:ro` mount causing issues?**
   - Lidarr might need write access to plugin directory
   - Test with `:rw` mount

5. **What happens if we add debug logging to Lidarr?**
   - Can we enable verbose plugin loading logs?
   - Would show exactly why plugin isn't loading

## Recommended Next Steps

### Step 1: Enable Debug Logging
Modify workflow to enable Lidarr debug logging:
```bash
docker exec lidarr-ss sed -i 's|<LogLevel>.*</LogLevel>|<LogLevel>Debug</LogLevel>|' /config/config.xml
docker restart lidarr-ss
```

### Step 2: Check Lidarr Plugin Loading Code
Find the plugin discovery/loading code in Lidarr plugins branch:
- How does it scan `/config/plugins/`?
- What does it look for?
- What errors does it log?

### Step 3: Compare with Working Plugin
Get TypNull's Tubifarry or TrevTV's plugins working in same container:
- Document what files they have
- Document what logs show on successful load
- Find the difference

### Step 4: Test Assembly Compatibility
Build plugin with explicit plugins branch assemblies:
- Extract assemblies from Docker container
- Build against those exact assemblies
- Test if plugin loads

### Step 5: Test `:rw` Mount
Change mount from read-only to read-write:
```yaml
-v "${{ github.workspace }}/plugin-dist:/config/plugins/Tidalarr:rw"
```

## Files to Reference

- `Lidarr.Plugin.Common/scripts/snapshots/snap.mjs` - Screenshot utility
- `tidalarr/.github/workflows/screenshots.yml` - Workflow definition
- `tidalarr/plugin.json` - Plugin manifest
- `Lidarr.Plugin.Common/.github/workflows/multi-plugin-smoke-test.yml` - Working build example
- Qobuzarr CLAUDE.md - Extensive assembly compatibility documentation

## Related PRs

### Merged (Fixed)
- Common #97, #98, #99 - snap.mjs improvements
- Tidalarr #52 - Mount path fix
- Qobuzarr #77 - Mount path fix
- Brainarr #325 - Mount path fix

### Still Open/Needed
- Investigation into plugin loading mechanism
- Possible TFM/assembly fixes
- Possible workflow restructuring

## Session Context

This investigation was conducted on 2025-12-05. The snap.mjs and mount path fixes were completed and merged. The plugin loading issue was identified but not resolved due to its complexity requiring deeper investigation into Lidarr's plugin branch internals.

The issue likely requires:
1. Understanding Lidarr plugins branch plugin discovery code
2. Building against correct assemblies for plugins branch
3. Possibly restructuring how plugins are packaged/deployed

This is NOT a snap.mjs issue anymore - it's a fundamental plugin compatibility/loading issue.
