using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class ConditionalGetRevalidationTests
    {
        private sealed class InMemoryConditionalState : IConditionalRequestState
        {
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? etag, DateTimeOffset? lm)> _map = new();
            public async ValueTask<(string? ETag, DateTimeOffset? LastModified)?> TryGetValidatorsAsync(string cacheKey, CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                if (_map.TryGetValue(cacheKey, out var v)) return (v.etag, v.lm);
                return null;
            }
            public ValueTask SetValidatorsAsync(string cacheKey, string? etag, DateTimeOffset? lastModified, CancellationToken cancellationToken = default)
            {
                _map[cacheKey] = (etag, lastModified);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class PolicyProvider : IResiliencePolicyProvider, ICachePolicyProvider
        {
            private readonly ResilienceProfileSettings _r = new()
            {
                Name = "default",
                MaxRetries = 2,
                RetryBudget = TimeSpan.FromSeconds(1),
                MaxConcurrencyPerHost = 4,
                PerRequestTimeout = null
            };
            private readonly CachePolicy _c;
            public PolicyProvider(CachePolicy c) { _c = c; }
            public ResilienceProfileSettings Get(string profileName) => _r;
            public CachePolicy GetPolicy(string endpoint, System.Collections.Generic.IReadOnlyDictionary<string, string> parameters) => _c;
        }

        private sealed class ETagHandler : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var ifNoneMatch = request.Headers.IfNoneMatch;
                if (ifNoneMatch != null && ifNoneMatch.Count > 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                }
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}")
                };
                resp.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                resp.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
                resp.Content.Headers.LastModified = DateTimeOffset.UtcNow;
                return Task.FromResult(resp);
            }
        }

        [Fact]
        public async Task Revalidation_304_Refreshes_TTL_And_Preserves_Body()
        {
            // Use a small TTL and advance virtual time deterministically to drive revalidation
            var cachePolicy = CachePolicy.Default.With(duration: TimeSpan.FromMilliseconds(100), enableConditionalRevalidation: true);
            var policyProvider = new PolicyProvider(cachePolicy);
            var cache = new TestCache(new NullLogger<StreamingResponseCache>(), new TestPolicyProvider(cachePolicy));
            var conditional = new InMemoryConditionalState();

            var handler = new ETagHandler();
            using var client = new HttpClient(handler);
            var req = new HttpRequestMessage(HttpMethod.Get, "https://etag.example/detail?id=1");
            req.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.EndpointKey, "/detail");
            req.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ParametersKey, "id=1");

            // First call populates cache and validators
            using var r1 = await client.ExecuteWithResilienceAndCachingAsync(req, policyProvider, cache, conditional, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

            // Advance time past TTL to force revalidation on next call
#if NET8_0_OR_GREATER
            cache.Advance(TimeSpan.FromMilliseconds(150));
#endif

            using var r2 = await client.ExecuteWithResilienceAndCachingAsync(req, policyProvider, cache, conditional, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode); // synthetic 200 from cache
            var headerVals = r2.Headers.GetValues(Lidarr.Plugin.Common.Services.Caching.ArrCachingHeaders.RevalidatedHeader);
            Assert.Contains(Lidarr.Plugin.Common.Services.Caching.ArrCachingHeaders.RevalidatedValue, headerVals);

            // Advance again (less than refreshed TTL) and ensure cache still holds value
#if NET8_0_OR_GREATER
            cache.Advance(TimeSpan.FromMilliseconds(50));
#endif
            var cached = cache.Get<CachedHttpResponse>("/detail", new System.Collections.Generic.Dictionary<string, string> { { "id", "1" } });
            Assert.NotNull(cached);
        }

        private sealed class TestCache : StreamingResponseCache
        {
#if NET8_0_OR_GREATER
            private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _tp;
            public TestCache(Microsoft.Extensions.Logging.ILogger logger, ICachePolicyProvider provider)
                : this(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow), logger, provider) { }

            private TestCache(Microsoft.Extensions.Time.Testing.FakeTimeProvider tp, Microsoft.Extensions.Logging.ILogger logger, ICachePolicyProvider provider)
                : base(tp, logger, provider)
            {
                _tp = tp;
            }
            public void Advance(TimeSpan by) => _tp.Advance(by);
#else
            public TestCache(Microsoft.Extensions.Logging.ILogger logger, ICachePolicyProvider provider) : base(logger, provider) { }
#endif
            protected override string GetServiceName() => "E";
        }

        private sealed class TestPolicyProvider : ICachePolicyProvider
        {
            private readonly CachePolicy _policy;
            public TestPolicyProvider(CachePolicy p) { _policy = p; }
            public CachePolicy GetPolicy(string endpoint, System.Collections.Generic.IReadOnlyDictionary<string, string> parameters) => _policy;
        }
    }
}
