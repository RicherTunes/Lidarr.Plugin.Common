# Quickstart: Build a Plugin

This walkthrough gets you from zero to a running plugin using the isolation architecture. Keep this page short and link out for details.

## Prerequisites
- .NET SDK 8.0 or later (install via `dotnet --list-sdks`).
- Access to the host-provided `Lidarr.Plugin.Abstractions` NuGet feed.
- The latest `Lidarr.Plugin.Common` package version you plan to ship.

## 1. Scaffold a project
```bash
mkdir MyPlugin && cd MyPlugin
dotnet new classlib -n MyPlugin -f net8.0
```
Add references:
```xml
<ItemGroup>
  <PackageReference Include="Lidarr.Plugin.Abstractions" Version="1.0.0" PrivateAssets="all" ExcludeAssets="runtime;native;contentfiles" />
  <PackageReference Include="Lidarr.Plugin.Common" Version="1.1.4" />
</ItemGroup>
<PropertyGroup>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>
```
See [How to: create a plugin project](../how-to/CREATE_PLUGIN.md) for the full template.

## 2. Implement `IPlugin`
Use Abstractions interfaces only for cross-boundary types.
```csharp
public sealed class MyPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "myplugin",
        Name = "My Plugin",
        Version = "1.0.0",
        ApiVersion = "1.x",
        MinHostVersion = "2.12.0"
    };

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken token = default)
        => ValueTask.CompletedTask;

    public ValueTask<IIndexer?> CreateIndexerAsync(CancellationToken token = default)
        => ValueTask.FromResult<IIndexer?>(new MyIndexer(context.LoggerFactory.CreateLogger<MyIndexer>()));

    public ValueTask<IDownloadClient?> CreateDownloadClientAsync(CancellationToken token = default)
        => ValueTask.FromResult<IDownloadClient?>(null);

    public ISettingsProvider SettingsProvider { get; } = new MySettingsProvider();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```
Extend with [Implement an indexer](../how-to/IMPLEMENT_INDEXER.md) and other how-to guides as you add features.

## 3. Package dependencies
Ensure the final folder looks like this:
```
/Plugins/MyPlugin/
  MyPlugin.dll
  Lidarr.Plugin.Common.dll
  third-party.dll
  plugin.json
```
Run `dotnet publish -c Release` or copy the build output manually. Never rely on the host to ship Common or your third-party assemblies.

## 4. Author `plugin.json`
```json
{
  "id": "myplugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "apiVersion": "1.x",
  "commonVersion": "1.1.4",
  "minHostVersion": "2.12.0",
  "entryAssembly": "MyPlugin.dll"
}
```
See the [Manifest reference](../reference/MANIFEST.md) for the full schema and validation rules.

## 5. Test in isolation
- Run unit tests (`dotnet test`).
- Use the [isolation loader sample](../examples/ISOLATION_HOST_SAMPLE.md) to load your plugin in its own ALC.
- Verify manifest compatibility errors fail fast.

## 6. Ship
- Package the folder per host instructions.
- Document required settings for users ([Settings reference](../reference/SETTINGS.md)).
- Watch release notes for Abstractions updates ([Compatibility matrix](../concepts/COMPATIBILITY.md)).

That’s it. You now have an isolated plugin that can live alongside other plugins without conflicts.
