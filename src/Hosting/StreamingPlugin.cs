using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Manifest;
using Lidarr.Plugin.Common.Hosting.Settings;
using Lidarr.Plugin.Common.Services.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Hosting;

/// <summary>
/// Base implementation of <see cref="IPlugin"/> that wires strongly typed settings and
/// dependency injection into the new host abstractions. Plugin authors derive from this class
/// and focus on domain logic (indexer/download client) instead of ceremony.
/// </summary>
/// <typeparam name="TModule">Module describing service registrations and metadata.</typeparam>
/// <typeparam name="TSettings">Strongly typed settings object for the plugin.</typeparam>
public abstract class StreamingPlugin<TModule, TSettings> : IPlugin
    where TModule : StreamingPluginModule, new()
    where TSettings : class, new()
{
    private readonly object _settingsLock = new();
    private readonly TModule _module;

    private IServiceProvider? _serviceProvider;
    private IServiceScopeFactory? _scopeFactory;
    private StreamingSettingsProvider? _settingsProvider;
    private PluginManifest? _manifest;
    private TSettings? _settings;

    protected StreamingPlugin()
    {
        _module = CreateModule() ?? throw new InvalidOperationException("Module creation returned null.");
    }

    /// <summary>
    /// Creates the plugin module instance. Override to provide a custom module implementation.
    /// </summary>
    protected virtual TModule CreateModule() => new();

    /// <summary>
    /// Loaded plugin manifest. Available after <see cref="InitializeAsync"/> completes.
    /// </summary>
    public PluginManifest Manifest => _manifest ?? throw new InvalidOperationException("Plugin not initialized.");

    /// <summary>
    /// Strongly typed settings instance shared across the plugin AssemblyLoadContext.
    /// </summary>
    protected TSettings Settings => _settings ?? throw new InvalidOperationException("Plugin not initialized.");

    /// <summary>
    /// Root service provider for dependency injection.
    /// </summary>
    protected IServiceProvider Services => _serviceProvider ?? throw new InvalidOperationException("Plugin not initialized.");

    /// <summary>
    /// Exposes host-facing settings provider implemented by the bridge.
    /// </summary>
    public ISettingsProvider SettingsProvider => _settingsProvider ?? throw new InvalidOperationException("Plugin not initialized.");

    /// <summary>
    /// Name of the manifest file to load relative to the plugin assembly folder.
    /// </summary>
    protected virtual string ManifestFileName => "plugin.json";

    /// <summary>
    /// Override to add or modify DI registrations after the module has configured the collection.
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services, IPluginContext context, TSettings settings)
    {
    }

    /// <summary>
    /// Override to provide metadata for host UI (defaults to empty).
    /// </summary>
    protected virtual IEnumerable<SettingDefinition> DescribeSettings() => Array.Empty<SettingDefinition>();

    /// <summary>
    /// Override to configure additional defaults after <typeparamref name="TSettings"/> is constructed.
    /// </summary>
    protected virtual void ConfigureDefaults(TSettings settings)
    {
    }

    /// <summary>
    /// Override to validate settings and return warnings/errors. Defaults to success.
    /// </summary>
    protected virtual PluginValidationResult ValidateSettings(TSettings settings) => PluginValidationResult.Success();

    /// <summary>
    /// Called after settings are applied to allow modules to react (e.g., refresh caches).
    /// </summary>
    protected virtual void OnSettingsApplied(TSettings settings)
    {
    }

    /// <summary>
    /// Optional async initialization hook executed after DI is built but before the plugin is ready.
    /// </summary>
    protected virtual ValueTask OnInitializedAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Helper for derived classes to create a scoped service provider.
    /// </summary>
    protected IServiceScope CreateScope()
    {
        return (_scopeFactory ?? throw new InvalidOperationException("Plugin not initialized.")).CreateScope();
    }

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var assemblyLocation = GetType().Assembly.Location;
        var assemblyDirectory = string.IsNullOrEmpty(assemblyLocation)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;

        var manifestPath = Path.Combine(assemblyDirectory, ManifestFileName);
        _manifest = PluginManifest.Load(manifestPath);

        _settings = CreateSettingsInstance();
        _module.RegisterServices();

        var services = _module.CreateServiceCollection(collection =>
        {
            RegisterCoreServices(collection, context, _settings);
            ConfigureServices(collection, context, _settings);
        });

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        _scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _settingsProvider = new StreamingSettingsProvider(this);

        await OnInitializedAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IIndexer?> CreateIndexerAsync(CancellationToken cancellationToken = default)
    {
        return CreateIndexerAsync(Settings, Services, cancellationToken);
    }

    public ValueTask<IDownloadClient?> CreateDownloadClientAsync(CancellationToken cancellationToken = default)
    {
        return CreateDownloadClientAsync(Settings, Services, cancellationToken);
    }

    /// <summary>
    /// Override to create the host-facing indexer implementation. Return <c>null</c> when unsupported.
    /// </summary>
    protected virtual ValueTask<IIndexer?> CreateIndexerAsync(TSettings settings, IServiceProvider services, CancellationToken cancellationToken)
        => ValueTask.FromResult<IIndexer?>(null);

    /// <summary>
    /// Override to create the host-facing download client implementation. Return <c>null</c> when unsupported.
    /// </summary>
    protected virtual ValueTask<IDownloadClient?> CreateDownloadClientAsync(TSettings settings, IServiceProvider services, CancellationToken cancellationToken)
        => ValueTask.FromResult<IDownloadClient?>(null);

    public virtual async ValueTask DisposeAsync()
    {
        if (_settingsProvider is not null)
        {
            await _settingsProvider.DisposeAsync().ConfigureAwait(false);
            _settingsProvider = null;
        }

        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            (_serviceProvider as IDisposable)?.Dispose();
        }

        _module.Dispose();
    }

    private TSettings CreateSettingsInstance()
    {
        var settings = new TSettings();
        ConfigureDefaults(settings);
        return settings;
    }

    private void RegisterCoreServices(IServiceCollection services, IPluginContext context, TSettings settings)
    {
        services.AddSingleton(context);
        services.AddSingleton(context.LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(settings);
        services.AddSingleton<ISettingsProvider>(_ => _settingsProvider ?? throw new InvalidOperationException("Settings provider not ready."));
    }

    private sealed class StreamingSettingsProvider : ISettingsProvider, IAsyncDisposable
    {
        private readonly StreamingPlugin<TModule, TSettings> _plugin;

        public StreamingSettingsProvider(StreamingPlugin<TModule, TSettings> plugin)
        {
            _plugin = plugin;
        }

        public IReadOnlyCollection<SettingDefinition> Describe()
        {
            return _plugin.DescribeSettings() is IReadOnlyCollection<SettingDefinition> collection
                ? collection
                : new List<SettingDefinition>(_plugin.DescribeSettings());
        }

        public IReadOnlyDictionary<string, object?> GetDefaults()
        {
            var defaults = new TSettings();
            _plugin.ConfigureDefaults(defaults);
            return SettingsBinder.ToDictionary(defaults);
        }

        public PluginValidationResult Validate(IDictionary<string, object?> settings)
        {
            var current = _plugin.Settings;
            var snapshot = SettingsBinder.Clone(current);
            SettingsBinder.Populate(settings, snapshot);
            return _plugin.ValidateSettings(snapshot);
        }

        public PluginValidationResult Apply(IDictionary<string, object?> settings)
        {
            lock (_plugin._settingsLock)
            {
                var current = _plugin.Settings;
                var snapshot = SettingsBinder.Clone(current);
                SettingsBinder.Populate(settings, snapshot);
                var result = _plugin.ValidateSettings(snapshot);
                if (!result.IsValid)
                {
                    return result;
                }

                SettingsBinder.Copy(snapshot, current);
                _plugin.OnSettingsApplied(current);
                return result;
            }
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
