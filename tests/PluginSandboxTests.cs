using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.TestKit.Assertions;
using Lidarr.Plugin.Common.TestKit.Data;
using Lidarr.Plugin.Common.TestKit.Fixtures;
using Lidarr.Plugin.Common.TestKit.Http;
using Lidarr.Plugin.Common.Tests.Isolation;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public sealed class PluginSandboxTests
    {
        [Fact]
        public async Task PluginSandbox_loads_plugin_and_releases_locks()
        {
            using var builder = new TestPluginBuilder();
            var pluginDir = builder.BuildPlugin("SandboxPlugin", "1.2.2");
            var pluginPath = Path.Combine(pluginDir, "SandboxPlugin.dll");

            await using (var sandbox = await PluginSandbox.CreateAsync(pluginPath))
            {
                Assert.NotNull(sandbox.Plugin.Manifest);
                Assert.Equal("SandboxPlugin", sandbox.Plugin.Manifest.Name);
                Assert.False(string.IsNullOrWhiteSpace(sandbox.Context.HostVersion.ToString()));
            }

            ForceGarbageCollection();
        }

        [Fact]
        public async Task Gzip_handler_roundtrips_payload()
        {
            using var handler = new GzipMislabeledHandler("{\"status\":\"ok\"}");
            using var client = new HttpClient(handler, disposeHandler: true);
            var data = await client.GetByteArrayAsync("https://example.test/diagnostics");

            using var buffer = new MemoryStream(data);
            using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var json = reader.ReadToEnd();

            using var document = JsonDocument.Parse(json);
            Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        }

        [Fact]
        public void Embedded_json_is_available()
        {
            using var document = EmbeddedJson.Open("Tidal/track-preview.json");
            Assert.Equal("Signal (Preview)", document.RootElement.GetProperty("title").GetString());
        }

        [Fact]
        public void Plugin_assertions_detect_success()
        {
            var result = new DownloadResult { Success = true };
            PluginAssertions.AssertSuccess(result);
        }

        private static void ForceGarbageCollection()
        {
            for (var i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }
}
