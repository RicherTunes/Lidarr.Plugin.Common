using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Hosting;
using Lidarr.Plugin.Abstractions.Manifest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace IsolationHostSample
{
    internal static class Program
    {
        public static async Task Main(string[] args)
        {
            var pluginsRoot = Path.Combine(AppContext.BaseDirectory, "plugins");
            var hostVersion = new Version(2, 12, 0, 0);
            var ciMode = false;
            var createComponents = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (string.Equals(arg, "--ci", StringComparison.OrdinalIgnoreCase))
                {
                    ciMode = true;
                    continue;
                }

                if (string.Equals(arg, "--create-components", StringComparison.OrdinalIgnoreCase))
                {
                    createComponents = true;
                    continue;
                }

                if (string.Equals(arg, "--host-version", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Missing value for --host-version.");
                        Environment.ExitCode = 2;
                        return;
                    }

                    if (!Version.TryParse(args[i + 1], out hostVersion))
                    {
                        Console.Error.WriteLine($"Invalid host version '{args[i + 1]}'.");
                        Environment.ExitCode = 2;
                        return;
                    }

                    i++;
                    continue;
                }

                if (!arg.StartsWith("--", StringComparison.Ordinal) && pluginsRoot == Path.Combine(AppContext.BaseDirectory, "plugins"))
                {
                    pluginsRoot = arg;
                }
            }

            if (ciMode && hostVersion == new Version(2, 12, 0, 0))
            {
                hostVersion = new Version(99, 0, 0, 0);
            }

            if (!Directory.Exists(pluginsRoot))
            {
                Console.Error.WriteLine($"No plugins folder found at '{pluginsRoot}'.");
                Environment.ExitCode = 1;
                return;
            }

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddSimpleConsole(options =>
                {
                    options.ColorBehavior = LoggerColorBehavior.Enabled;
                    options.IncludeScopes = false;
                    options.SingleLine = true;
                });
            });

            var contractVersion = typeof(IPlugin).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

            var pluginDirectories = Directory.EnumerateDirectories(pluginsRoot).ToList();
            if (pluginDirectories.Count == 0)
            {
                Console.Error.WriteLine($"No plugin directories found inside '{pluginsRoot}'.");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine($"Loading {pluginDirectories.Count} plugin(s) from '{pluginsRoot}'.");
            var handles = new List<PluginHandle>();
            var failed = false;

            try
            {
                foreach (var pluginDirectory in pluginDirectories)
                {
                    // snippet:alc-loader
                    var request = new PluginLoadRequest
                    {
                        PluginDirectory = pluginDirectory,
                        HostVersion = hostVersion,
                        ContractVersion = contractVersion,
                        PluginContext = new DefaultPluginContext(hostVersion, loggerFactory),
                        SharedAssemblies = new[]
                        {
                            "Lidarr.Plugin.Abstractions",
                            "Microsoft.Extensions.Logging.Abstractions"
                        }
                    };

                    Console.WriteLine($"-> Loading '{Path.GetFileName(pluginDirectory)}'...");

                    try
                    {
                        var handle = await PluginLoader.LoadAsync(request).ConfigureAwait(false);
                        handles.Add(handle);

                        Console.WriteLine($"   Manifest: {handle.Plugin.Manifest.Name} v{handle.Plugin.Manifest.Version} (Common {handle.Plugin.Manifest.CommonVersion ?? "n/a"})");

                        if (createComponents)
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                            var indexer = await handle.Plugin.CreateIndexerAsync(cts.Token).ConfigureAwait(false);
                            if (indexer is not null)
                            {
                                await indexer.DisposeAsync().ConfigureAwait(false);
                                Console.WriteLine("   Created indexer instance.");
                            }

                            var downloadClient = await handle.Plugin.CreateDownloadClientAsync(cts.Token).ConfigureAwait(false);
                            if (downloadClient is not null)
                            {
                                await downloadClient.DisposeAsync().ConfigureAwait(false);
                                Console.WriteLine("   Created download client instance.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failed = true;
                        Console.Error.WriteLine($"   Failed to load '{Path.GetFileName(pluginDirectory)}': {ex.Message}");
                    }
                    // end-snippet
                }

                if (!ciMode)
                {
                    Console.WriteLine();
                    Console.WriteLine("All plugins loaded. Press ENTER to unload and exit.");
                    Console.ReadLine();
                }
            }
            finally
            {
                foreach (var handle in handles)
                {
                    await handle.DisposeAsync().ConfigureAwait(false);
                }

                handles.Clear();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            if (failed)
            {
                Environment.ExitCode = 1;
            }
        }
    }

    internal sealed class ShimPluginExample : IPlugin
    {
        private PluginHandle? _payloadHandle;

        public PluginManifest Manifest { get; } = new()
        {
            Id = "shim.sample",
            Name = "Shim Sample",
            Version = "1.0.0",
            ApiVersion = "1.x",
            EntryAssembly = "ShimPlugin.dll"
        };

        // snippet:shim-plugin
        public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            var payloadDirectory = Path.Combine(Path.GetDirectoryName(typeof(ShimPluginExample).Assembly.Location)!, "payload");
            var request = new PluginLoadRequest
            {
                PluginDirectory = payloadDirectory,
                HostVersion = context.HostVersion,
                ContractVersion = typeof(IPlugin).Assembly.GetName().Version!,
                PluginContext = context,
                SharedAssemblies = new[]
                {
                    "Lidarr.Plugin.Abstractions",
                    "Microsoft.Extensions.DependencyInjection.Abstractions",
                    "Microsoft.Extensions.Logging.Abstractions"
                }
            };

            _payloadHandle = await PluginLoader.LoadAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<IIndexer?> CreateIndexerAsync(CancellationToken cancellationToken = default)
            => _payloadHandle?.Plugin.CreateIndexerAsync(cancellationToken) ?? ValueTask.FromResult<IIndexer?>(null);

        public ValueTask<IDownloadClient?> CreateDownloadClientAsync(CancellationToken cancellationToken = default)
            => _payloadHandle?.Plugin.CreateDownloadClientAsync(cancellationToken) ?? ValueTask.FromResult<IDownloadClient?>(null);

        public ISettingsProvider SettingsProvider
            => _payloadHandle?.Plugin.SettingsProvider ?? throw new InvalidOperationException("Payload not initialised");

        public ValueTask DisposeAsync()
            => _payloadHandle?.DisposeAsync() ?? ValueTask.CompletedTask;
        // end-snippet
    }
}
