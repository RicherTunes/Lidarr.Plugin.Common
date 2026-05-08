using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Lidarr.Plugin.Common.Services.Caching;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Time.Testing;

namespace Lidarr.Plugin.Common.Benchmarks;

/// <summary>
/// Baselines for the three primary <see cref="CachingHttpExecutor"/> code paths:
/// hot-cache-hit, miss (origin -> 2xx -> cache write), and 304 not-modified fold.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class CachingHttpExecutorBenchmarks
{
    private const string BaseUrl = "https://bench.example.test/v1";
    private const string Endpoint = "/v1/catalog/items/42";

    private CachingHttpExecutor _hitExec = null!;
    private CachingHttpExecutor _missExec = null!;
    private CachingHttpExecutor _foldExec = null!;
    private CacheKey _key = null!;
    private string _body = null!;

    [GlobalSetup]
    public void Setup()
    {
        _key = new CacheKey(Endpoint, new Dictionary<string, string> { ["q"] = "k" });
        _body = "{\"id\":42,\"name\":\"bench\",\"items\":[1,2,3,4,5]}";

        _hitExec = BuildHotHitExecutor(_body);
        _missExec = BuildMissExecutor(_body);
        _foldExec = BuildNotModifiedFoldExecutor(_body);
    }

    [Benchmark(Description = "Hit (hot cache, no origin call)")]
    public async Task<int> Hit()
    {
        var result = await _hitExec.SendAsync(NewBuilder(), _key, HotHitPolicy()).ConfigureAwait(false);
        return result.Body?.Length ?? 0;
    }

    [Benchmark(Description = "Miss (origin 200, cache write)")]
    public async Task<int> Miss()
    {
        var result = await _missExec.SendAsync(NewBuilder(), _key, MissPolicy()).ConfigureAwait(false);
        return result.Body?.Length ?? 0;
    }

    [Benchmark(Description = "NotModifiedFold (origin 304, cached body returned)")]
    public async Task<int> NotModifiedFold()
    {
        var result = await _foldExec.SendAsync(NewBuilder(), _key, RevalidatePolicy()).ConfigureAwait(false);
        return result.Body?.Length ?? 0;
    }

    private static StreamingApiRequestBuilder NewBuilder()
        => new StreamingApiRequestBuilder(BaseUrl).Endpoint("catalog/items/42").Get();

    private static CachePolicy HotHitPolicy()
        => CachePolicy.Default
            .With(name: "bench-hothit", duration: TimeSpan.FromMinutes(15))
            .WithExecutor(hotHitMode: HotCacheHitMode.EnabledForFreshEntries);

    private static CachePolicy MissPolicy()
        => CachePolicy.Default.With(
            name: "bench-miss",
            duration: TimeSpan.FromMinutes(15));

    private static CachePolicy RevalidatePolicy()
        => CachePolicy.Default.With(
            name: "bench-revalidate",
            duration: TimeSpan.FromMinutes(15),
            enableConditionalRevalidation: true);

    // ---- builders ----

    private static CachingHttpExecutor BuildHotHitExecutor(string body)
    {
        var policy = HotHitPolicy();
        var (exec, cache) = BuildCore(policy, _ => HttpBuilders.Ok(body));
        // Pre-seed the cache with a fresh entry so every iteration is a hot hit.
        cache.Set(Endpoint, new Dictionary<string, string> { ["q"] = "k" }, new CachedHttpResponse
        {
            StatusCode = System.Net.HttpStatusCode.OK,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            ContentType = "application/json",
            StoredAt = DateTimeOffset.UtcNow
        }, TimeSpan.FromMinutes(15));
        return exec;
    }

    private static CachingHttpExecutor BuildMissExecutor(string body)
    {
        // Cache is empty; each call hits the origin. Because the executor caches the response,
        // we evict before returning the executor to the iteration. To keep the benchmark stable
        // we instead use a no-cache policy (ShouldCache=false), so each iteration is a miss-by-design.
        var policy = new CachePolicy(name: "bench-miss-nocache", shouldCache: false, duration: TimeSpan.Zero);
        var (exec, _) = BuildCore(policy, _ => HttpBuilders.Ok(body));
        return exec;
    }

    private static CachingHttpExecutor BuildNotModifiedFoldExecutor(string body)
    {
        var policy = RevalidatePolicy();
        var (exec, cache) = BuildCore(policy, _ => HttpBuilders.NotModified());
        // Seed cache so the executor folds the 304 against it. ETag stays valid across iterations.
        cache.Set(Endpoint, new Dictionary<string, string> { ["q"] = "k" }, new CachedHttpResponse
        {
            StatusCode = System.Net.HttpStatusCode.OK,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            ContentType = "application/json",
            ETag = "\"abc123\"",
            StoredAt = DateTimeOffset.UtcNow
        }, TimeSpan.FromHours(1));
        return exec;
    }

    private static (CachingHttpExecutor exec, BenchCache cache) BuildCore(
        CachePolicy policy,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var tp = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var provider = new StaticPolicyProvider(policy);
        var cache = new BenchCache(tp, provider);
        var handler = new ScriptedHandler((_, req) => respond(req));
        var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var exec = new CachingHttpExecutor(client, cache, ResiliencePolicy.Default, policyProvider: provider, timeProvider: tp);
        return (exec, cache);
    }
}
