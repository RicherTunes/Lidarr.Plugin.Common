# Plugin Isolation Playbook

Treat the host-facing ABI as a tiny contract (`Lidarr.Plugin.Abstractions`) and keep everything else plugin-private. This document captures the concrete steps for host maintainers and plugin authors adopting AssemblyLoadContext (ALC) isolation.

## Why isolation?
- .NET loads only one assembly per simple name inside a given ALC. Without isolation, whichever plugin loads `Lidarr.Plugin.Common` first wins, forcing the rest to use the same version.
- Collectible ALCs let the host unload or upgrade plugins without restarting the process.
- Keeping only `Lidarr.Plugin.Abstractions` shared ensures casts across contexts succeed while implementation details remain private.

## Host loader quickstart
1. Reference `Lidarr.Plugin.Abstractions` and point the loader at a plugins directory (`/Plugins/*`).
2. Share only the contract assemblies (Abstractions and any optional logging abstractions).
3. Spin up a `PluginLoadContext` per plugin and dispose/unload it when the plugin is removed or upgraded.

```csharp
var hostVersion = new Version(2, 12, 0, 0);
var contractVersion = typeof(IPlugin).Assembly.GetName().Version!;
var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());

foreach (var directory in Directory.EnumerateDirectories(pluginRoot))
{
    var request = new PluginLoadRequest
    {
        PluginDirectory = directory,
        HostVersion = hostVersion,
        ContractVersion = contractVersion,
        PluginContext = new DefaultPluginContext(hostVersion, loggerFactory),
        SharedAssemblies = new[] { "Lidarr.Plugin.Abstractions", "Microsoft.Extensions.Logging.Abstractions" }
    };

    await using var handle = await PluginLoader.LoadAsync(request);
    Console.WriteLine($"Loaded {handle.Plugin.Manifest.Name} (Common {handle.Plugin.Manifest.CommonVersion})");
}
```

> A runnable version of this loader lives in `examples/IsolationHostSample/`.

## Plugin packaging checklist
- Reference Abstractions with `PrivateAssets="all"` so the host supplies the runtime copy.
- Ship `Lidarr.Plugin.Common` (and every other dependency) next to the plugin DLL.
- Enable `CopyLocalLockFileAssemblies` or use `dotnet publish` to gather private binaries.
- Include `plugin.json` with:
  - `apiVersion`: Abstractions major expected by the plugin (e.g., `1.x`).
  - `minHostVersion`: lowest Lidarr host version supported.
  - `commonVersion`: informational diagnostics only.

## Shim/proxy plugin (when the host cannot change)
When the host loader is fixed, ship a tiny stub that immediately spins up a private ALC and forwards to a payload assembly.

```csharp
public sealed class ShimPlugin : IPlugin
{
    private PluginHandle? _payload;

    public PluginManifest Manifest { get; } = new()
    {
        Id = "shim.sample",
        Name = "Shim Sample",
        Version = "1.0.0",
        ApiVersion = "1.x",
        EntryAssembly = "ShimPlugin.dll"
    };

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken token = default)
    {
        var payloadDir = Path.Combine(Path.GetDirectoryName(typeof(ShimPlugin).Assembly.Location)!, "payload");
        var request = new PluginLoadRequest
        {
            PluginDirectory = payloadDir,
            HostVersion = context.HostVersion,
            ContractVersion = typeof(IPlugin).Assembly.GetName().Version!,
            PluginContext = context,
            SharedAssemblies = new[] { "Lidarr.Plugin.Abstractions", "Microsoft.Extensions.Logging.Abstractions" }
        };

        _payload = await PluginLoader.LoadAsync(request, token);
    }

    public ValueTask<IIndexer?> CreateIndexerAsync(CancellationToken token = default)
        => _payload?.Plugin.CreateIndexerAsync(token) ?? ValueTask.FromResult<IIndexer?>(null);

    public ValueTask<IDownloadClient?> CreateDownloadClientAsync(CancellationToken token = default)
        => _payload?.Plugin.CreateDownloadClientAsync(token) ?? ValueTask.FromResult<IDownloadClient?>(null);

    public ISettingsProvider SettingsProvider => _payload?.Plugin.SettingsProvider ?? throw new InvalidOperationException("Payload not initialised");

    public ValueTask DisposeAsync() => _payload?.DisposeAsync() ?? ValueTask.CompletedTask;
}
```

Payload layout:
```
/Plugins/MyShim/
  ShimPlugin.dll           # host loads this
  plugin.json              # apiVersion targets Abstractions 1.x
  /payload/
    RealPlugin.dll         # references Lidarr.Plugin.Common
    Lidarr.Plugin.Common.dll
    third-party.dll
```

## Test the setup
- `PluginIsolationTests` and the Roslyn-based `TestPluginBuilder` (see `tests/Isolation/`) generate synthetic plugins with different Common versions to validate side-by-side loading.
- `PluginLoaderEdgeTests` ensure missing manifests, invalid JSON, and min-host mismatches fail fast with clear messages.

Keep the ABI small, isolate everything else, and Lidarr can load dozens of independently versioned plugins without conflict.
