# Lidarr.Plugin.Abstractions Overview

`Lidarr.Plugin.Abstractions` is the tiny host-owned contract shared between the Lidarr process and every streaming plugin. Keep it boring, stable, and versioned independently from `Lidarr.Plugin.Common`.

## What lives here?

- **Contracts**: `IPlugin`, `IIndexer`, `IDownloadClient`, `ISettingsProvider`, `IPluginContext`, and `PluginValidationResult`.
- **Hosting helpers**: `PluginLoader`, `PluginLoadContext`, `PluginLoadRequest`, `PluginHandle`, and `DefaultPluginContext` for hosts/tests.
- **Manifest objects**: `PluginManifest` + `PluginCompatibilityResult` for parsing/validating `plugin.json`.
- **Shared DTOs**: Lightweight streaming models (`StreamingArtist`, `StreamingAlbum`, `StreamingTrack`, etc.) and download models to keep cross-context types consistent.

Everything else stays in the plugin-owned `Lidarr.Plugin.Common` and must not leak across AssemblyLoadContexts.

## Package usage

```xml

<ItemGroup>
  <!-- Compile-time only: runtime copy is provided by the host -->
  <PackageReference Include="Lidarr.Plugin.Abstractions" Version="1.0.0" PrivateAssets="all" ExcludeAssets="runtime;native;contentfiles" />
</ItemGroup>

```

Because the host loads Abstractions into the default AssemblyLoadContext, plugins should never ship their own runtime copy.

## Versioning policy

- **Semantic versioning** with a fixed `AssemblyVersion` per major (1.0.0.0 for all 1.x).
- **Minor releases** are additive only (new optional members, default interface methods, default implementations).
- **Patch releases** are bug fixes or doc updates.
- **Major releases** are rare and require a coordinated migration plan.

Use `dotnet format analyzers` + the `Microsoft.CodeAnalysis.PublicApiAnalyzers` baseline (`src/Abstractions/PublicAPI.*`) to ensure inadvertent breaking changes are caught in CI.

## Testing and validation

- `tests/PluginManifestTests.cs` and `tests/PluginLoaderEdgeTests.cs` cover manifest parsing, compatibility checks, and failure cases.
- `tests/PluginIsolationTests.cs` verifies Abstractions-only sharing works across collectible AssemblyLoadContexts.

If you add or modify Abstractions APIs, update the PublicAPI baseline and extend the tests above to cover new behavior.

