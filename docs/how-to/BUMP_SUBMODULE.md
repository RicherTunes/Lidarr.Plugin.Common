# How-to: Bump the lidarr.plugin.common submodule

This checklist is what downstream repos (e.g., Brainarr) need to upgrade safely.

## 1. Read the latest release note
Each tag publishes a one-paragraph summary in [docs/UPGRADING.md](../UPGRADING.md) and the matching GitHub Release. Skim Breaking/Deprecations/Dependencies before you bump.

## 2. Update the submodule to the tagged release
```powershell
# Fetch the latest tags
git submodule foreach git fetch --tags

# Move the submodule to the new tag (example: v1.1.5)
git submodule update --remote ext/lidarr.plugin.common
git -C ext/lidarr.plugin.common checkout v1.1.5

# Stage the submodule pointer
git add ext/lidarr.plugin.common
```

> Tip: replace `v1.1.5` with the tag announced in the release note.

## 3. Run your plugin tests
Execute the same test suite that guards your plugin, especially the streaming harness described in [USE_STREAMING_PLUGIN.md](USE_STREAMING_PLUGIN.md).

## 4. Package or smoke test as needed
Use the shared packaging module without `-MergeAssemblies` unless your release process requires a single DLL:
```powershell
Import-Module ../tools/PluginPack.psm1
New-PluginPackage -Csproj plugins/MyPlugin/MyPlugin.csproj -Manifest plugins/MyPlugin/plugin.json
```

## 5. Commit with traceable metadata
```bash
git commit -m "chore(common): bump to v1.1.5"
```

Include a link to the GitHub release (or changelog entry) in the commit body if your contribution guidelines require it.

Keeping to these steps means every bump is one command, one commit, and fully auditable.
