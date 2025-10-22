using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Hosting;
using Lidarr.Plugin.Abstractions.Manifest;
using Lidarr.Plugin.Common.Tests.Isolation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class PluginLoaderEdgeTests
    {
        private static readonly Version HostVersion = new(2, 12, 0, 0);
        private static readonly Version ContractVersion = typeof(IPlugin).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

        private static DefaultPluginContext CreateContext() => new(HostVersion, NullLoggerFactory.Instance);

        [Fact]
        public async Task LoadAsync_throws_when_manifest_missing()
        {
            using var builder = new TestPluginBuilder();
            var pluginDir = builder.BuildPlugin("MissingManifest", "1.2.2");
            File.Delete(Path.Combine(pluginDir, "plugin.json"));

            var request = CreateRequest(pluginDir);

            await Assert.ThrowsAsync<FileNotFoundException>(() => PluginLoader.LoadAsync(request));
        }

        [Fact]
        public async Task LoadAsync_throws_when_manifest_invalid_json()
        {
            using var builder = new TestPluginBuilder();
            var pluginDir = builder.BuildPlugin("BadManifest", "1.2.2");
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), "{ notValidJson }");

            var request = CreateRequest(pluginDir);

            await Assert.ThrowsAsync<JsonException>(() => PluginLoader.LoadAsync(request));
        }

        [Fact]
        public async Task LoadAsync_rejects_when_host_version_too_low()
        {
            using var builder = new TestPluginBuilder();
            var pluginDir = builder.BuildPlugin("HostTooLow", "1.2.2");

            var manifestPath = Path.Combine(pluginDir, "plugin.json");
            var manifest = PluginManifest.Load(manifestPath);
            var strict = new PluginManifest
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Version = manifest.Version,
                ApiVersion = manifest.ApiVersion,
                MinHostVersion = "9.9.9",
                EntryAssembly = manifest.EntryAssembly,
                CommonVersion = manifest.CommonVersion,
                Capabilities = manifest.Capabilities,
                RequiredSettings = manifest.RequiredSettings
            };
            File.WriteAllText(manifestPath, strict.ToJson());

            var request = CreateRequest(pluginDir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PluginLoader.LoadAsync(request));
            Assert.Contains("Host version", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task LoadAsync_throws_when_entry_assembly_missing()
        {
            using var builder = new TestPluginBuilder();
            var pluginDir = builder.BuildPlugin("MissingAssembly", "1.2.2");
            File.Delete(Path.Combine(pluginDir, "MissingAssembly.dll"));

            var request = CreateRequest(pluginDir);

            await Assert.ThrowsAsync<FileNotFoundException>(() => PluginLoader.LoadAsync(request));
        }

        private static PluginLoadRequest CreateRequest(string pluginDirectory)
        {
            return new PluginLoadRequest
            {
                PluginDirectory = pluginDirectory,
                HostVersion = HostVersion,
                ContractVersion = ContractVersion,
                PluginContext = CreateContext()
            };
        }
    }
}
