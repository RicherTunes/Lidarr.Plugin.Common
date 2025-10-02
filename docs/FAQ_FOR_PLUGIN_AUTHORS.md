# FAQ for Plugin Authors

## Why am I seeing duplicate `Microsoft.Extensions.*` warnings when I publish?
Because the publish folder contains both plugin-owned copies and host-provided assemblies. Stick to folder-based packaging (no merge) or, if you merge, rely on the provided `ilrepack.rsp` which strips `Microsoft.*` binaries before merging. Never include `Lidarr.Plugin.Abstractions` in the payload.

## Do I merge `Lidarr.Plugin.Abstractions` into my plugin?
No. The host owns that assembly. `PluginPack.psm1` removes it automatically so you do not ship it, and the default `internalize.exclude` file keeps it out of ILRepack runs.

## How do I test `StreamingPlugin<TModule, TSettings>` locally?
Follow the harness in [How-to: Test `StreamingPlugin<TModule, TSettings>`](how-to/USE_STREAMING_PLUGIN.md). Build your plugin with `dotnet publish`, run the xUnit fixture, and expose DI services via a simple `IServiceProviderAccessor` interface.

## What feeds do I need in `NuGet.config`?
Only `nuget.org` (plus the TagLib feed used by the host). Build and CI succeed without preview feeds. If you must add previews for your own tooling, gate them behind CI-specific environment variables.

## RS0016/RS0017 analyzers fail locally. What now?
Use the public API baselining scripts:

```powershell
# Update shipped/unshipped baselines after intentional API changes
./tools/Update-PublicApiBaselines.ps1 -Project src/Abstractions/Lidarr.Plugin.Abstractions.csproj
```

Run this under both `net6.0` and `net8.0` when you add or remove public surface area.

## Packaging script is noisy (warnings or unexpected DLLs). How do I diagnose it?
Run `New-PluginPackage` with `-Verbose` to inspect each step. Verify the publish directory contains only plugin-owned assemblies before packaging, and confirm `ManifestCheck.ps1` passes to catch mismatched versions early.
