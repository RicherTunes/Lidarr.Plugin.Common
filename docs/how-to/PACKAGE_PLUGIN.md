# How-to: Package a Plugin

Use the shared packaging helpers to build, validate, and zip a plugin without copy/paste scripts in every repository.

## 1. Validate the manifest

```powershell
pwsh ./tools/ManifestCheck.ps1 `
    -ProjectPath plugins/Tidalarr/Tidalarr.csproj `
    -ManifestPath plugins/Tidalarr/plugin.json
```

Validation rules:

- `version` in `plugin.json` must match the project `Version`.
- `apiVersion` must use `major.x` and match the referenced `Lidarr.Plugin.Abstractions` major.
- Warns if `minHostVersion` or `targets` are missing/mismatched.

## 2. Build and zip

```powershell
Import-Module ./tools/PluginPack.psm1
New-PluginPackage `
    -Csproj plugins/Tidalarr/Tidalarr.csproj `
    -Manifest plugins/Tidalarr/plugin.json `
    -Framework net8.0 `
    -Configuration Release
```

Outputs a zip in `artifacts/packages/` (for example `Tidalarr-2.3.0-net8.0.zip`). The helper:

- Runs `dotnet publish` with `CopyLocalLockFileAssemblies=true` so private dependencies sit next to the plugin DLL.
- Reuses manifest validation.
- Strips host-owned assemblies (`Lidarr.Plugin.Abstractions*.dll`) to avoid leaking shared binaries.

## 3. CI integration

Add steps in your GitHub Actions workflow:

```yaml
- name: Validate manifest
  shell: pwsh
  run: ./tools/ManifestCheck.ps1 -ProjectPath plugins/Tidalarr/Tidalarr.csproj -ManifestPath plugins/Tidalarr/plugin.json

- name: Package plugin artifacts
  shell: pwsh
  run: |
    Import-Module ./tools/PluginPack.psm1
    New-PluginPackage -Csproj plugins/Tidalarr/Tidalarr.csproj -Manifest plugins/Tidalarr/plugin.json -Framework net8.0 -Configuration Release
  working-directory: ${{ github.workspace }}

- name: Upload plugin bundle
  uses: actions/upload-artifact@v4
  with:
    name: tidalarr-plugin
    path: plugins/Tidalarr/artifacts/packages/*.zip
```

## 4. Multi-target plugins

Call `New-PluginPackage` once per TFM (e.g., `net6.0` and `net8.0`). The script keeps the publish output separated by framework and produces distinct zip files.

## 5. Troubleshooting

- **Missing manifest**: ensure `plugin.json` is copied to the publish directory (`Copy to Output Directory = Always`).
- **Unexpected assemblies in zip**: review the publish folder; the helper only strips Abstractions. Add extra `Remove-Item` steps in your repo for service-specific exclusions.
- **Version mismatch**: bump both the project `Version` and the manifest before packaging.

Pair this guide with [Use the streaming plugin bridge](USE_STREAMING_PLUGIN.md) and [Test a plugin with the isolation loader](TEST_PLUGIN.md) to cover the full build → test → package loop.
