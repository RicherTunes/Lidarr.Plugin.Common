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
    public class DedupCancellationNoCacheTests
    {
        private sealed class BlockUntilCancelledHandler : DelegatingHandler
        {
            private int _callCount;
            private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int CallCount => Volatile.Read(ref _callCount);
            public Task Started => _started.Task;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _callCount);
                _started.TrySetResult();

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            }
        }

        private sealed class TestCache : StreamingResponseCache
        {
            public TestCache(ICachePolicyProvider provider) : base(new NullLogger<StreamingResponseCache>(), provider) { }
            protected override string GetServiceName() => "TestService";
        }

        private sealed class PolicyProvider : IResiliencePolicyProvider, ICachePolicyProvider
        {
            public ResilienceProfileSettings Get(string profileName) => new ResilienceProfileSettings
            {
                Name = profileName,
                MaxRetries = 0,
                RetryBudget = TimeSpan.FromSeconds(1),
                MaxConcurrencyPerHost = 2,
                MaxTotalConcurrencyPerHost = 2,
                PerRequestTimeout = null
            };
            public CachePolicy GetPolicy(string endpoint, IReadOnlyDictionary<string, string> parameters) => CachePolicy.Default.With(duration: TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task Cancel_During_Upstream_Prevents_Cache_Write_And_Cleans_Dedup()
        {
            var handler = new BlockUntilCancelledHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://cancel.test/") };
            var provider = new PolicyProvider();
            var cache = new TestCache(provider);
            var deduper = new Lidarr.Plugin.Common.Services.Deduplication.RequestDeduplicator(NullLogger<Lidarr.Plugin.Common.Services.Deduplication.RequestDeduplicator>.Instance);

            var req1 = new HttpRequestMessage(HttpMethod.Get, "https://cancel.test/res?q=1");
            req1.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.EndpointKey, "/res");
            req1.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ParametersKey, "q=1");

            var req2 = new HttpRequestMessage(HttpMethod.Get, "https://cancel.test/res?q=1");
            req2.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.EndpointKey, "/res");
            req2.Options.Set(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ParametersKey, "q=1");

            using var cts = new CancellationTokenSource();

            var t1 = client.ExecuteWithResilienceAndCachingAsync(req1, provider, cache, deduper, cancellationToken: cts.Token);
            var t2 = client.ExecuteWithResilienceAndCachingAsync(req2, provider, cache, deduper, cancellationToken: cts.Token);

            await handler.Started;
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);

            // Ensure nothing got cached
            var cached = cache.Get<HttpResponseMessage>("/res", new Dictionary<string, string> { { "q", "1" } });
            Assert.Null(cached);

            // Ensure dedup registry is clean
            var stats = deduper.GetStatistics();
            Assert.Equal(0, stats.ActiveRequests);
        }
    }
}
