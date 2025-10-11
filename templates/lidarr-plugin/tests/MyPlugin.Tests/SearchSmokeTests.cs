using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Http;
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
}

