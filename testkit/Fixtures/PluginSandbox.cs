using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Hosting;
using Lidarr.Plugin.Common.TestKit.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidarr.Plugin.Common.TestKit.Fixtures;

/// <summary>
/// Utility that loads a streaming plugin inside a collectible <see cref="AssemblyLoadContext"/>
/// so tests can exercise real entry points without leaking dependencies into the host.
/// </summary>
public sealed class PluginSandbox : IAsyncDisposable
{
    private readonly PluginLoadContext _loadContext;
    private readonly string _pluginAssemblyPath;
    private bool _disposed;

    private PluginSandbox(PluginLoadContext loadContext, IPlugin plugin, PluginTestContext context, string pluginAssemblyPath)
    {
        _loadContext = loadContext;
        Plugin = plugin;
        Context = context;
        _pluginAssemblyPath = pluginAssemblyPath;
    }

    /// <summary>The plugin instance created inside the sandbox.</summary>
    public IPlugin Plugin { get; }

    /// <summary>Host-facing context supplied to the plugin.</summary>
    public PluginTestContext Context { get; }

    /// <summary>Loads a plugin assembly into an isolated context and initializes it.</summary>
    public static async Task<PluginSandbox> CreateAsync(string pluginAssemblyPath, PluginSandboxOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginAssemblyPath))
        {
            throw new ArgumentException("Plugin assembly path must be provided", nameof(pluginAssemblyPath));
        }

        if (!File.Exists(pluginAssemblyPath))
        {
            throw new FileNotFoundException("Plugin assembly not found", pluginAssemblyPath);
        }

        options ??= new PluginSandboxOptions();

        var loadContext = new PluginLoadContext(pluginAssemblyPath, options.SharedAssemblies, isCollectible: true);
        using (loadContext.EnterContextualReflection())
        {
            var assembly = loadContext.LoadFromAssemblyPath(pluginAssemblyPath);
            var pluginType = assembly.GetTypes().FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);
            if (pluginType is null)
            {
                throw new InvalidOperationException($"Assembly '{pluginAssemblyPath}' does not contain a concrete IPlugin implementation.");
            }

            var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
            var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
            var services = options.Services ?? (options.ConfigureServices is null
                ? null
                : BuildServiceProvider(options.ConfigureServices));
            var context = options.Context ?? new PluginTestContext(options.HostVersion ?? PluginSandboxOptions.DefaultHostVersion, loggerFactory, services);

            await plugin.InitializeAsync(context, cancellationToken).ConfigureAwait(false);

            return new PluginSandbox(loadContext, plugin, context, pluginAssemblyPath);
        }

        static IServiceProvider BuildServiceProvider(Action<IServiceCollection> configure)
        {
            var services = new ServiceCollection();
            configure(services);
            return services.BuildServiceProvider();
        }
    }

    /// <summary>Creates an indexer from the plugin if available.</summary>
    public ValueTask<IIndexer?> CreateIndexerAsync(CancellationToken cancellationToken = default)
        => Plugin.CreateIndexerAsync(cancellationToken);

    /// <summary>Creates a download client from the plugin if available.</summary>
    public ValueTask<IDownloadClient?> CreateDownloadClientAsync(CancellationToken cancellationToken = default)
        => Plugin.CreateDownloadClientAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await Plugin.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _loadContext.Unload();
            ForceGarbageCollection();
        }
    }

    private void ForceGarbageCollection()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}

/// <summary>Configuration options for <see cref="PluginSandbox"/>.</summary>
public sealed class PluginSandboxOptions
{
    public static readonly Version DefaultHostVersion = new(2, 14, 0, 0);

    /// <summary>Overrides the host context used during initialization.</summary>
    public PluginTestContext? Context { get; init; }

    /// <summary>Host version exposed to the plugin when <see cref="Context"/> is not set.</summary>
    public Version? HostVersion { get; init; }

    /// <summary>Optional service provider shared with the plugin.</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>Optional delegate used to build a service provider when <see cref="Services"/> is null.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Logger factory shared across the sandbox. Defaults to <see cref="NullLoggerFactory"/>.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>Fully qualified assembly names that should be resolved from the default ALC.</summary>
    public string[]? SharedAssemblies { get; init; }
}
