# Packaging Plugins

`tools/PluginPack.psm1` standardises how plugins are published: build, validate manifests, strip host-owned assemblies, and zip artifacts for distribution. The default flow is **folder-based** packaging; merging is optional and opt-in.

## Folder packaging (recommended)

```powershell file=../tools/PluginPack.psm1#plugin-pack
```

Usage inside a plugin repository:

```powershell
Import-Module ../tools/PluginPack.psm1
$artifact = New-PluginPackage -Csproj plugins/Tidalarr/Tidalarr.csproj -Manifest plugins/Tidalarr/plugin.json
Write-Host "Created $artifact"
```

This produces `artifacts/packages/<pluginId>-<version>-<tfm>.zip` containing the publish folder after cleanup. No ILRepack step runs, dependencies remain side-by-side with the plugin, the package ships a **canonical** `Lidarr.Plugin.Abstractions.dll` (identical bytes across plugins), and host-shared assemblies that cause type-identity conflicts are stripped.

## Optional: merge plugin-private assemblies

Some teams prefer a single DLL payload. Enable that per build:

```powershell
New-PluginPackage -Csproj plugins/Tidalarr/Tidalarr.csproj `
                  -Manifest plugins/Tidalarr/plugin.json `
                  -MergeAssemblies `
                  -IlRepackRsp ../tools/ilrepack.rsp `
                  -InternalizeExclude ../tools/internalize.exclude
```

Defaults baked into `ilrepack.rsp`:
- Runs in parallel with wildcard support.
- Uses the publish directory as the `/lib` path.
- Writes to `<AssemblyName>.merged.dll` and swaps it into place.
- Excludes `System.*`, `Microsoft.*`, and `Lidarr.Plugin.Abstractions` from merge candidates (Abstractions must remain a separate DLL).

Tweak the `.rsp` or exclude file if you need extra filters, but keep `Lidarr.Plugin.Abstractions` out of the merged output.

## CI flow

```mermaid
flowchart TD
  A[Commit/PR] --> CI[Build + Test (net6/net8)]
  CI --> Pack[Run PluginPack.psm1]
  Pack --> Publish[Tag  NuGet push]
  Publish --> Consumers[Plugins pick up new Common]
```

## Checklist

1. `dotnet publish` into a plugin-local folder with `CopyLocalLockFileAssemblies=true`.
2. Run `ManifestCheck.ps1` to ensure the manifest matches the project file.
3. Ensure the payload contains the canonical `Lidarr.Plugin.Abstractions.dll` and does **not** ship host-shared assemblies (notably `Microsoft.Extensions.*.Abstractions.dll`).
4. Zip the folder as `<PluginId>-<Version>-<TFM>.zip` and upload as a release asset.
5. Keep README/CHANGELOG alongside the package for discovery—other docs stay in the repo.

## Contract

- Every plugin repository should import and call `New-PluginPackage` in CI before publishing.
- Packaging must fail if the manifest is out of sync with the project file.
- The packed plugin must contain only plugin-owned assemblies plus `plugin.json`.
- Hosts load plugins from their directory without additional installation steps.
- Update this document whenever packaging scripts or conventions change.
