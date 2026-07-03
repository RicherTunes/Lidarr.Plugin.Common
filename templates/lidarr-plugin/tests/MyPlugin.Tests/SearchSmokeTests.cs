using System.Net;
using System.Net.Http;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.TestKit.Fixtures;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace MyPlugin.Tests;

public class SearchSmokeTests
{
    private sealed class StubHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task Builder_Options_Executor_Path_Works()
    {
        var factory = new HttpClient(new StubHandler()) { BaseAddress = new System.Uri("https://example") };
        var req = new StreamingApiRequestBuilder(factory.BaseAddress!.ToString())
            .Endpoint("v1/search")
            .Get()
            .Query("q", "beatles")
            .WithStreamingDefaults()
            .WithPolicy(ResiliencePolicy.Search)
            .Build();

        using var resp = await factory.ExecuteWithResilienceAsync(req, ResiliencePolicy.Search);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Generated_Plugin_Loads_In_Isolated_Sandbox()
    {
        var assemblyPath = typeof(MyPluginPlugin).Assembly.Location;
        var pluginJson = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "plugin.json");

        Assert.True(File.Exists(pluginJson), $"Expected plugin.json next to {assemblyPath}");

        await using var sandbox = await PluginSandbox.CreateAsync(assemblyPath);
        Assert.Equal("MyPlugin", sandbox.Plugin.Manifest.Name);
    }
}
