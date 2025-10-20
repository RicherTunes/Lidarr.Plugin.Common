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
    public class ConditionalGetLastModifiedOnlyTests
    {
        private sealed class PolicyProvider : IResiliencePolicyProvider, ICachePolicyProvider
        {
            private readonly ResilienceProfileSettings _r = new()
            {
                Name = "default",
                MaxRetries = 2,
                RetryBudget = TimeSpan.FromSeconds(1),
                MaxConcurrencyPerHost = 2,
                PerRequestTimeout = null
            };
            private readonly CachePolicy _c;
            public PolicyProvider(CachePolicy c) { _c = c; }
            public ResilienceProfileSettings Get(string profileName) => _r;
            public CachePolicy GetPolicy(string endpoint, System.Collections.Generic.IReadOnlyDictionary<string, string> parameters) => _c;
        }

        private sealed class LastModifiedHandler : DelegatingHandler
        {
            private DateTimeOffset? _last;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Headers.IfModifiedSince.HasValue && _last.HasValue)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                }
                var now = DateTimeOffset.UtcNow;
                _last = now;
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}")
                };
                resp.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                resp.Content.Headers.LastModified = now;
                return Task.FromResult(resp);
            }
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
            protected override string GetServiceName() => "LM";
        }

        [Fact]
        public async Task Auto_Revalidation_Uses_LastModified_When_Enabled()
        {
            var cachePolicy = CachePolicy.Default.With(duration: TimeSpan.FromMilliseconds(100), enableConditionalRevalidation: true);
            var policyProvider = new PolicyProvider(cachePolicy);
            var cache = new TestCache(new NullLogger<StreamingResponseCache>(), policyProvider);

            var handler = new LastModifiedHandler();
            using var client = new HttpClient(handler);
            var req = new HttpRequestMessage(HttpMethod.Get, "https://lm.example/detail?id=42");
            req.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.EndpointKey, "/detail");
            req.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ParametersKey, "id=42");

            // First call populates cache with Last-Modified only
            using var r1 = await client.ExecuteWithResilienceAndCachingAsync(req, policyProvider, cache, conditionalState: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

            // Revalidate: advance time past TTL so pipeline revalidates with If-Modified-Since
#if NET8_0_OR_GREATER
            cache.Advance(TimeSpan.FromMilliseconds(150));
#endif
            using var r2 = await client.ExecuteWithResilienceAndCachingAsync(req, policyProvider, cache, conditionalState: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode); // synthetic 200 produced from cached body
            var headerVals = r2.Headers.GetValues(Lidarr.Plugin.Common.Services.Caching.ArrCachingHeaders.RevalidatedHeader);
            Assert.Contains(Lidarr.Plugin.Common.Services.Caching.ArrCachingHeaders.RevalidatedValue, headerVals);
        }
    }
}
