using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace IsolationHostSample
{
    internal static class Program
    {
        public static async Task Main(string[] args)
        {
            var pluginsRoot = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "plugins");
            if (!Directory.Exists(pluginsRoot))
            {
                Console.WriteLine($"No plugins folder found at '{pluginsRoot}'.");
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

            var hostVersion = new Version(2, 12, 0, 0);
            var contractVersion = typeof(IPlugin).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

            var pluginDirectories = Directory.EnumerateDirectories(pluginsRoot).ToList();
            if (pluginDirectories.Count == 0)
            {
                Console.WriteLine($"No plugin directories found inside '{pluginsRoot}'.");
                return;
            }

            Console.WriteLine($"Loading {pluginDirectories.Count} plugin(s) from '{pluginsRoot}'.");
            var handles = new List<PluginHandle>();

            try
            {
                foreach (var pluginDirectory in pluginDirectories)
                {
                    var request = new PluginLoadRequest
                    {
                        PluginDirectory = pluginDirectory,
                        HostVersion = hostVersion,
                        ContractVersion = contractVersion,
                        PluginContext = new DefaultPluginContext(hostVersion, loggerFactory),
                        SharedAssemblies = new[] { "Lidarr.Plugin.Abstractions", "Microsoft.Extensions.Logging.Abstractions" }
                    };

                    Console.WriteLine($"-> Loading '{Path.GetFileName(pluginDirectory)}'...");
                    var handle = await PluginLoader.LoadAsync(request).ConfigureAwait(false);
                    handles.Add(handle);

                    Console.WriteLine($"   Manifest: {handle.Plugin.Manifest.Name} v{handle.Plugin.Manifest.Version} (Common {handle.Plugin.Manifest.CommonVersion ?? "n/a"})");
                }

                Console.WriteLine();
                Console.WriteLine("All plugins loaded. Press ENTER to unload and exit.");
                Console.ReadLine();
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
        }
    }
}
