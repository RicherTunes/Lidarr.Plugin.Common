using System;
using System.Collections.Generic;
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

            var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;

            Type pluginType;
            if (options.PluginType is not null)
            {
                if (!typeof(IPlugin).IsAssignableFrom(options.PluginType))
                    throw new ArgumentException($"PluginType '{options.PluginType.FullName}' does not implement IPlugin.");
                if (options.PluginType.IsAbstract)
                    throw new ArgumentException($"PluginType '{options.PluginType.FullName}' is abstract.");
                pluginType = options.PluginType;
            }
            else
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    if (options.LoaderMode == SandboxLoaderMode.Strict)
                        throw; // Fail fast — runtime-proof tests should not tolerate loader failures

                    // Permissive: recover but surface the failures
                    foreach (var loaderEx in ex.LoaderExceptions.Where(e => e is not null))
                    {
                        loggerFactory.CreateLogger<PluginSandbox>()
                            .LogWarning(loaderEx, "Loader exception (permissive mode): {Type}", loaderEx!.GetType().Name);
                    }

                    types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
                }

                List<Type> pluginTypes = types.Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract).ToList();
                if (pluginTypes.Count == 0)
                {
                    throw new InvalidOperationException($"Assembly '{pluginAssemblyPath}' does not contain a concrete IPlugin implementation.");
                }

                if (pluginTypes.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"Assembly contains {pluginTypes.Count} concrete IPlugin implementations: " +
                        $"{string.Join(", ", pluginTypes.Select(t => t.FullName))}. Exactly one is required. " +
                        $"Set {nameof(PluginSandboxOptions)}.{nameof(PluginSandboxOptions.PluginType)} to select one explicitly.");
                }

                pluginType = pluginTypes[0];
            }

            var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
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

/// <summary>
/// Controls how <see cref="PluginSandbox"/> handles <see cref="ReflectionTypeLoadException"/>
/// when scanning the plugin assembly for <see cref="IPlugin"/> implementations.
/// </summary>
public enum SandboxLoaderMode
{
    /// <summary>Fail if any types fail to load. Default for runtime-proof tests.</summary>
    Strict,

    /// <summary>Allow partial type recovery but log loader exceptions. Use for known ILRepack scenarios.</summary>
    Permissive
}

/// <summary>Configuration options for <see cref="PluginSandbox"/>.</summary>
public sealed class PluginSandboxOptions
{
    public static readonly Version DefaultHostVersion = new(3, 1, 2, 4913);

    /// <summary>
    /// When set, the sandbox uses this type directly instead of scanning the assembly.
    /// Useful when an assembly contains multiple concrete <see cref="IPlugin"/> implementations.
    /// </summary>
    public Type? PluginType { get; init; }

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

    /// <summary>
    /// Controls how the sandbox handles <see cref="ReflectionTypeLoadException"/> during type scanning.
    /// <see cref="SandboxLoaderMode.Strict"/> (default) lets the exception propagate, failing the test.
    /// <see cref="SandboxLoaderMode.Permissive"/> recovers surviving types and logs loader exceptions.
    /// </summary>
    public SandboxLoaderMode LoaderMode { get; init; } = SandboxLoaderMode.Strict;
}
