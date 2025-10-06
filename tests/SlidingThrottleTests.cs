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
    public class SlidingThrottleTests
    {
        private sealed class PolicyProvider : ICachePolicyProvider
        {
            private readonly CachePolicy _policy;
            public PolicyProvider(CachePolicy policy) => _policy = policy;
            public CachePolicy GetPolicy(string endpoint, IReadOnlyDictionary<string, string> parameters) => _policy;
        }

        private sealed class TestCache : StreamingResponseCache
        {
            public int Extensions;
            public TestCache(ICachePolicyProvider provider) : base(new NullLogger<StreamingResponseCache>(), provider) { }
            protected override string GetServiceName() => "TestService";
            protected override void OnSlidingExtended(string endpoint, string cacheKey, DateTime previousExpiry, DateTime newExpiry)
            {
                Interlocked.Increment(ref Extensions);
            }
        }

        private sealed class Payload { public string Id { get; init; } = string.Empty; }

        [Fact]
        public async Task SlidingRefreshWindow_Coalesces_Extensions()
        {
            var baseDuration = TimeSpan.FromMilliseconds(120);
            var sliding = TimeSpan.FromMilliseconds(150);
            var window = TimeSpan.FromMilliseconds(75);
            var policy = CachePolicy.Default.With(duration: baseDuration, slidingExpiration: sliding, slidingRefreshWindow: window);
            var cache = new TestCache(new PolicyProvider(policy));

            var parameters = new Dictionary<string, string> { { "id", "42" } };
            cache.Set("detail", parameters, new Payload { Id = "answer" });

            // Blast 50 concurrent hits within the window; at most one extension should be recorded
            var tasks = Enumerable.Range(0, 50)
                .Select(_ => Task.Run(() => cache.Get<Payload>("detail", parameters)))
                .ToArray();
            await Task.WhenAll(tasks);

            Assert.InRange(cache.Extensions, 0, 1);
        }
    }
}

