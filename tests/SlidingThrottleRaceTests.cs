using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class SlidingThrottleRaceTests
    {
        private sealed class TestCache : StreamingResponseCache
        {
            public int ExtendedCount;
            public TestCache(ICachePolicyProvider provider) : base(new NullLogger<StreamingResponseCache>(), provider) { }
            protected override string GetServiceName() => "TestService";
            protected override void OnSlidingExtended(string endpoint, string cacheKey, DateTime previousExpiry, DateTime newExpiry)
            {
                Interlocked.Increment(ref ExtendedCount);
            }
        }

        private sealed class PolicyProvider : ICachePolicyProvider
        {
            private readonly CachePolicy policy;
            public PolicyProvider(CachePolicy policy) => this.policy = policy;
            public CachePolicy GetPolicy(string endpoint, IReadOnlyDictionary<string, string> parameters) => policy;
        }

        private sealed class Payload { public string Id { get; init; } = string.Empty; }

        [Fact]
        public async Task Sliding_Throttle_Coalesces_Extend_Once_Per_Window()
        {
            var sliding = TimeSpan.FromMilliseconds(100);
            var window = TimeSpan.FromMilliseconds(200);
            var duration = TimeSpan.FromSeconds(2);
            var policy = CachePolicy.Default.With(duration: duration, slidingExpiration: sliding, slidingRefreshWindow: window);
            var cache = new TestCache(new PolicyProvider(policy));
            var endpoint = "detail";
            var parameters = new Dictionary<string, string> { { "id", "1" } };

            cache.Set(endpoint, parameters, new Payload { Id = "one" });

            // Wait so that the first extension is allowed (past window since creation)
            await Task.Delay(window + TimeSpan.FromMilliseconds(20));

            // Burst of concurrent hits – should extend at most once within the window
            var tasks = Enumerable.Range(0, 50)
                .Select(_ => Task.Run(() => cache.Get<Payload>(endpoint, parameters)))
                .ToArray();
            await Task.WhenAll(tasks);

            Assert.True(cache.ExtendedCount <= 1, $"Extended {cache.ExtendedCount} times; expected coalesced <= 1 within window");
        }
    }
}

