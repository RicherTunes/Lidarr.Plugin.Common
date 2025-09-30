# How-to: Use the Streaming Plugin Bridge

StreamingPlugin<TModule, TSettings> removes most of the boilerplate you had to write to satisfy IPlugin. Derive from it, register your services in a StreamingPluginModule, and focus on business logic.

## 1. Define settings

`csharp
using Lidarr.Plugin.Common.Base;

public sealed class TidalarrSettings : BaseStreamingSettings
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
`

Defaults come from the base type (cache duration, rate limits, country code). Override ConfigureDefaults in the bridge if you need different values.

## 2. Register services in a module

`csharp
using Lidarr.Plugin.Common.Services.Registration;
using Microsoft.Extensions.DependencyInjection;

public sealed class TidalarrModule : StreamingPluginModule
{
    public override string ServiceName => "Tidal";
    public override string Description => "Lossless streaming provider";
    public override string Author => "RicherTunes";

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<TidalApiClient>();
        services.AddTransient<TidalIndexer>();    // implements BaseStreamingIndexer<TidalarrSettings>
        services.AddTransient<TidalDownloadClient>(); // implements BaseStreamingDownloadClient<TidalarrSettings>
    }
}
`

## 3. Derive from the bridge

`csharp
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Hosting;

public sealed class TidalarrPlugin : StreamingPlugin<TidalarrModule, TidalarrSettings>
{
    protected override IEnumerable<SettingDefinition> DescribeSettings()
    {
        yield return new SettingDefinition
        {
            Key = nameof(TidalarrSettings.ClientId),
            DisplayName = "Client ID",
            Description = "OAuth client identifier issued by Tidal",
            DataType = SettingDataType.String,
            IsRequired = true
        };
        yield return new SettingDefinition
        {
            Key = nameof(TidalarrSettings.ClientSecret),
            DisplayName = "Client Secret",
            Description = "OAuth client secret",
            DataType = SettingDataType.Password,
            IsRequired = true
        };
    }

    protected override PluginValidationResult ValidateSettings(TidalarrSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            return PluginValidationResult.Failure(new[] { "ClientId is required." });
        }

        if (string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            return PluginValidationResult.Failure(new[] { "ClientSecret is required." });
        }

        return PluginValidationResult.Success();
    }

    protected override ValueTask<IIndexer?> CreateIndexerAsync(TidalarrSettings settings, IServiceProvider services, CancellationToken ct)
    {
        // Resolve your adapter that implements Lidarr.Plugin.Abstractions.Contracts.IIndexer
        var indexer = services.GetRequiredService<TidalIndexerAdapter>();
        return ValueTask.FromResult<IIndexer?>(indexer);
    }

    protected override ValueTask<IDownloadClient?> CreateDownloadClientAsync(TidalarrSettings settings, IServiceProvider services, CancellationToken ct)
    {
        var client = services.GetRequiredService<TidalDownloadClientAdapter>();
        return ValueTask.FromResult<IDownloadClient?>(client);
    }
}
`

### What the bridge gives you

- Loads plugin.json automatically (defaults to the file next to the plugin assembly).
- Creates a singleton TSettings instance, applies defaults, and exposes it through DI.
- Implements ISettingsProvider using reflection, so the host sees key/value dictionaries while your code works with TSettings.
- Registers ILoggerFactory, typed ILogger<T>, and the host context inside your service provider.
- Keeps settings up to date: when the host calls Apply, the existing settings instance is mutated so any consumers resolve the new values.

### Lifecycle hooks you can override

| Method | Purpose |
|--------|---------|
| ConfigureDefaults(TSettings settings) | Adjust default values before anything resolves your settings. |
| DescribeSettings() | Return SettingDefinition entries for host UI. |
| ValidateSettings(TSettings settings) | Perform custom validation, return warnings or errors. |
| OnSettingsApplied(TSettings settings) | React to settings changes (e.g., refresh cached tokens). |
| ConfigureServices(IServiceCollection, IPluginContext, TSettings) | Add more DI registrations beyond the module. |
| OnInitializedAsync(IPluginContext, CancellationToken) | Async initialization after DI is ready. |

## Settings dictionary format

The bridge maps public writable properties on TSettings directly to dictionary keys (PascalCase by default). You can keep keys aligned with property names or transform them before returning SettingDefinition.DisplayName. Nested objects are not supported yet—prefer flat settings objects.

## Thread safety

The bridge updates the shared settings instance under a lock. If you cache values locally, subscribe to settings changes via OnSettingsApplied and refresh your caches there.

## Next steps

- Pair the bridge with the [settings guide](../reference/SETTINGS.md) to document each key.
- Use the [testing harness](TEST_PLUGIN.md) to exercise the plugin through PluginLoader.
- Add adapters from your existing BaseStreamingIndexer/BaseStreamingDownloadClient types to the new abstractions—start with simple wrappers, then share them across plugins.
