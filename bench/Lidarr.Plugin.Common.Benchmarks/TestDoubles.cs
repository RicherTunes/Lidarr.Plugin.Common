using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Lidarr.Plugin.Common.Benchmarks;

/// <summary>
/// Minimal in-memory cache used by the CachingHttpExecutor benchmarks. Mirrors the
/// pattern used by the existing CachingHttpExecutorTests.TestCache.
/// </summary>
internal sealed class BenchCache : StreamingResponseCache
{
    public BenchCache(FakeTimeProvider tp, ICachePolicyProvider provider)
        : base(tp, NullLogger<StreamingResponseCache>.Instance, provider)
    {
    }

    protected override string GetServiceName() => "Bench";
}

internal sealed class StaticPolicyProvider : ICachePolicyProvider
{
    private readonly CachePolicy _policy;

    public StaticPolicyProvider(CachePolicy policy) { _policy = policy; }

    public CachePolicy GetPolicy(string endpoint, IReadOnlyDictionary<string, string> parameters) => _policy;
}

/// <summary>
/// Deterministic <see cref="DelegatingHandler"/> that returns whatever the supplied factory
/// produces — independent of network and clock. Mirrors ScriptedHandler from the test suite
/// so benchmark numbers reflect executor/cache work rather than transport noise.
/// </summary>
internal sealed class ScriptedHandler : DelegatingHandler
{
    private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _factory;
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public ScriptedHandler(Func<int, HttpRequestMessage, HttpResponseMessage> factory)
    {
        _factory = factory;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _calls);
        return Task.FromResult(_factory(n, request));
    }
}

internal static class HttpBuilders
{
    public static HttpResponseMessage Ok(string body, string? etag = null, DateTimeOffset? lastModified = null, string contentType = "application/json")
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };
        resp.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (!string.IsNullOrEmpty(etag)) resp.Headers.ETag = new EntityTagHeaderValue(etag);
        if (lastModified.HasValue) resp.Content.Headers.LastModified = lastModified;
        return resp;
    }

    public static HttpResponseMessage NotModified()
        => new HttpResponseMessage(HttpStatusCode.NotModified);
}
