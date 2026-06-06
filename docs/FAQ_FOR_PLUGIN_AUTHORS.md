# FAQ for Plugin Authors

## Why am I seeing duplicate `Microsoft.Extensions.*` warnings when I publish?
Because the publish folder can contain both plugin-owned copies and host-shared assemblies. Stick to the ecosystem packaging pipeline (`tools/PluginPack.psm1`) so host-shared binaries are stripped consistently and you don’t accidentally ship type-identity conflicts.

## Do I merge `Lidarr.Plugin.Abstractions` into my plugin?
No. `Lidarr.Plugin.Abstractions` is the shared ABI contract loaded in the default AssemblyLoadContext, and it must remain a separate assembly (not merged/internalized).

In this ecosystem, plugins ship a **canonical** `Lidarr.Plugin.Abstractions.dll` (identical bytes across all plugins). `tools/PluginPack.psm1` enforces this via canonical injection and post-package SHA verification.

## How do I test `StreamingPlugin<TModule, TSettings>` locally?
Follow the harness in [How-to: Test `StreamingPlugin<TModule, TSettings>`](how-to/USE_STREAMING_PLUGIN.md). Build your plugin with `dotnet publish`, run the xUnit fixture, and expose DI services via a simple `IServiceProviderAccessor` interface.

## What feeds do I need in `NuGet.config`?
Only `nuget.org` (plus the TagLib feed used by the host). Build and CI succeed without preview feeds. If you must add previews for your own tooling, gate them behind CI-specific environment variables.

## Public API analyzers (RS0016/RS0017) — removed
The `Microsoft.CodeAnalysis.PublicApiAnalyzers` gate and its `PublicAPI.*.txt` baselines were removed 2026-06, so you will no longer see RS0016/RS0017 locally. Public-surface changes to Abstractions or Common are tracked through review and `CHANGELOG.md`; see [Public API baselines](reference/PUBLIC_API_BASELINES.md) for the rationale.

## Packaging script is noisy (warnings or unexpected DLLs). How do I diagnose it?
Run `New-PluginPackage` with `-Verbose` to inspect each step. Verify the publish directory contains only plugin-owned assemblies before packaging, and confirm `ManifestCheck.ps1` passes to catch mismatched versions early.
