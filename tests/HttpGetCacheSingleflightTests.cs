using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class HttpGetCacheSingleflightTests
    {
        private sealed class CountingHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
            public int Calls;

            public CountingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                return _handler(request, cancellationToken);
            }
        }

        private sealed class TestCache : StreamingResponseCache
        {
            public TestCache(ICachePolicyProvider provider) : base(new NullLogger<StreamingResponseCache>(), provider) { }
            protected override string GetServiceName() => "TestService";
        }

        private sealed class PolicyProvider : ICachePolicyProvider
        {
            private readonly CachePolicy _policy;
            public PolicyProvider(CachePolicy policy) => _policy = policy;
            public CachePolicy GetPolicy(string endpoint, IReadOnlyDictionary<string, string> parameters) => _policy;
        }

        [Fact]
        public async Task ThunderingHerd_Collapses_On_CacheMiss_And_Populates_Once()
        {
            var handler = new CountingHandler(async (req, ct) =>
            {
                await Task.Delay(25, ct);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ok")
                };
                return resp;
            });

            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://collapse.test/") };
            var policy = CachePolicy.Default.With(duration: TimeSpan.FromSeconds(5));
            var cache = new TestCache(new PolicyProvider(policy));
            var deduper = new Lidarr.Plugin.Common.Services.Deduplication.RequestDeduplicator(NullLogger<Lidarr.Plugin.Common.Services.Deduplication.RequestDeduplicator>.Instance);

            // Seed a canonicalized request with options so keys are stable
            var endpoint = "/search";
            var canonical = Lidarr.Plugin.Common.Utilities.QueryCanonicalizer.Canonicalize(new[]
            {
                new KeyValuePair<string, string>("q", "beatles"),
                new KeyValuePair<string, string>("a", "2"),
                new KeyValuePair<string, string>("a", "1"),
            });

            // Fire a burst of identical requests concurrently
            var tasks = new Task<HttpResponseMessage>[50];
            for (int i = 0; i < tasks.Length; i++)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://collapse.test/search?q=beatles&a=2&a=1");
                req.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.EndpointKey, endpoint);
                req.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ParametersKey, canonical);
                tasks[i] = client.ExecuteWithResilienceAndCachingAsync(req, new StubResilienceProvider(), cache, deduper, cancellationToken: CancellationToken.None);
            }

            await Task.WhenAll(tasks);

            Assert.Equal(1, handler.Calls);

            // Next single request should be served from cache without touching handler
            using var followupReq = new HttpRequestMessage(HttpMethod.Get, "https://collapse.test/search?q=beatles&a=2&a=1");
            followupReq.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.EndpointKey, endpoint);
            followupReq.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ParametersKey, canonical);
            using var followup = await client.ExecuteWithResilienceAndCachingAsync(followupReq, new StubResilienceProvider(), cache, deduper, cancellationToken: CancellationToken.None);
            Assert.Equal(1, handler.Calls);
        }

        private sealed class StubResilienceProvider : IResiliencePolicyProvider
        {
            public ResilienceProfileSettings Get(string profileName) => new ResilienceProfileSettings
            {
                Name = profileName,
                MaxRetries = 0,
                RetryBudget = TimeSpan.FromSeconds(1),
                MaxConcurrencyPerHost = 4,
                MaxTotalConcurrencyPerHost = 4,
                PerRequestTimeout = null
            };
        }
    }
}
