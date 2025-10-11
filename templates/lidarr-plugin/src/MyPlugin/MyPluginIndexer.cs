using System.Net.Http;
using System.Text.Json;
using Lidarr.Plugin.Common.Services.Http;

namespace MyPlugin;

public sealed class MyPluginIndexer
{
    private readonly HttpClient _http;

    public MyPluginIndexer(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("myplugin");
    }

    public async Task<JsonDocument> SearchAsync(string query, CancellationToken ct = default)
    {
        // Builder → Options → Executor flow
        var req = new StreamingApiRequestBuilder(_http.BaseAddress!.ToString())
            .Endpoint("v1/search")
            .Get()
            .Query("q", query)
            .WithStreamingDefaults(userAgent: "MyPlugin/1.0")
            .Build();

        using var resp = await _http.ExecuteWithResilienceAsync(req, ResiliencePolicy.Search, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }
}

