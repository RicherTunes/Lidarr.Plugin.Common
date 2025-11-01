using System;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Lidarr.Plugin.Common.Tests.Isolation;

namespace Lidarr.Plugin.Common.Tests
{
    public class PluginIsolationTests
    {
        private static Version HostVersion => new Version(2, 12, 0, 0);
        private static Version ContractVersion => typeof(IPlugin).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

        [Fact]
        public async Task Loads_plugins_with_side_by_side_common_versions()
        {
            using var builder = new TestPluginBuilder();
            var loggerFactory = NullLoggerFactory.Instance;
            var context = new DefaultPluginContext(HostVersion, loggerFactory);

            var pluginADir = builder.BuildPlugin("PluginA", "1.2.2");
            var pluginBDir = builder.BuildPlugin("PluginB", "1.0.9");

            await using var handleA = await PluginLoader.LoadAsync(new PluginLoadRequest
            {
                PluginDirectory = pluginADir,
                HostVersion = HostVersion,
                ContractVersion = ContractVersion,
                PluginContext = context
            });

            await using var handleB = await PluginLoader.LoadAsync(new PluginLoadRequest
            {
                PluginDirectory = pluginBDir,
                HostVersion = HostVersion,
                ContractVersion = ContractVersion,
                PluginContext = context
            });

            Assert.Equal("1.2.2", handleA.Plugin.Manifest.CommonVersion);
            Assert.Equal("1.0.9", handleB.Plugin.Manifest.CommonVersion);

            await using var indexerA = await handleA.Plugin.CreateIndexerAsync();
            await using var indexerB = await handleB.Plugin.CreateIndexerAsync();
            Assert.NotNull(indexerA);
            Assert.NotNull(indexerB);

            var trackA = (await indexerA!.SearchTracksAsync("proof"))[0];
            var trackB = (await indexerB!.SearchTracksAsync("proof"))[0];

            Assert.Equal("1.2.2", trackA.Album?.ExternalIds?["commonVersion"]);
            Assert.Equal("1.0.9", trackB.Album?.ExternalIds?["commonVersion"]);

            var commonAssemblyA = handleA.LoadContext.Assemblies.First(a => a.GetName().Name == "Lidarr.Plugin.Common");
            var commonAssemblyB = handleB.LoadContext.Assemblies.First(a => a.GetName().Name == "Lidarr.Plugin.Common");

            Assert.NotSame(commonAssemblyA, commonAssemblyB);
            Assert.NotEqual(commonAssemblyA.Location, commonAssemblyB.Location);
            Assert.NotEqual(commonAssemblyA.GetName().Version, commonAssemblyB.GetName().Version);
        }

        [Fact]
        public async Task Rejects_incompatible_api_version()
        {
            using var builder = new TestPluginBuilder();
            var loggerFactory = NullLoggerFactory.Instance;
            var context = new DefaultPluginContext(HostVersion, loggerFactory);

            var pluginDir = builder.BuildPlugin("PluginIncompatible", "1.2.3", apiVersion: "2.x");

            var request = new PluginLoadRequest
            {
                PluginDirectory = pluginDir,
                HostVersion = HostVersion,
                ContractVersion = ContractVersion,
                PluginContext = context
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PluginLoader.LoadAsync(request));
            Assert.Contains("abstractions major", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Unloads_plugin_context_on_dispose()
        {
            using var builder = new TestPluginBuilder();
            var loggerFactory = NullLoggerFactory.Instance;
            var context = new DefaultPluginContext(HostVersion, loggerFactory);
            var pluginDir = builder.BuildPlugin("PluginUnload", "1.3.0");

            PluginHandle? handle = await PluginLoader.LoadAsync(new PluginLoadRequest
            {
                PluginDirectory = pluginDir,
                HostVersion = HostVersion,
                ContractVersion = ContractVersion,
                PluginContext = context
            });

            var loadContext = handle.LoadContext;
            var weakRef = new WeakReference(loadContext, trackResurrection: false);

            await handle.DisposeAsync();
            handle = null;
            loadContext = null;

            for (var i = 0; i < 5 && weakRef.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(50);
            }

            Assert.False(weakRef.IsAlive);
        }
    }
}


