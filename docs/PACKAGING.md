# Packaging Plugins

`tools/PluginPack.psm1` standardises how plugins are published: build, validate manifests, strip host-owned assemblies, and zip artifacts for distribution.

## Build & package

```powershell file=../tools/PluginPack.psm1#plugin-pack
```

Usage inside a plugin repository:

```powershell
Import-Module ../tools/PluginPack.psm1
$artifact = New-PluginPackage -Csproj plugins/Tidalarr/Tidalarr.csproj -Manifest plugins/Tidalarr/plugin.json
Write-Host "Created $artifact"
```

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
3. Remove host-owned assemblies (`Lidarr.Plugin.Abstractions*`) from the payload.
4. Zip the folder as `<PluginId>-<Version>-<TFM>.zip` and upload as a release asset.
5. Keep README/CHANGELOG alongside the package for discovery—other docs stay in the repo.

## Contract

- Every plugin repository should import and call `New-PluginPackage` in CI before publishing.
- Packaging must fail if the manifest is out of sync with the project file.
- The packed plugin must contain only plugin-owned assemblies plus `plugin.json`.
- Hosts load plugins from their directory without additional installation steps.
- Update this document whenever packaging scripts or conventions change.
