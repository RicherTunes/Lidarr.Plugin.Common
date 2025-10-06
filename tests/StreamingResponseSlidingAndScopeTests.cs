using System;
using System.Collections.Generic;
using System.Threading;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class StreamingResponseSlidingAndScopeTests
    {
        private sealed class PolicyProvider : ICachePolicyProvider
        {
            private readonly CachePolicy policy;
            public PolicyProvider(CachePolicy policy) => this.policy = policy;
            public CachePolicy GetPolicy(string endpoint, IReadOnlyDictionary<string, string> parameters) => policy;
        }

        private sealed class TestCache : StreamingResponseCache
        {
            public TestCache(ICachePolicyProvider provider) : base(new NullLogger<StreamingResponseCache>(), provider) { }
            protected override string GetServiceName() => "TestService";
            public void SetMax(int size) => MaxCacheSize = size;
        }

        private sealed class Payload { public string Id { get; init; } = string.Empty; }

        [Fact]
        public void SlidingExpiration_Extends_Expiry_On_Hit()
        {
            var sliding = TimeSpan.FromMilliseconds(150);
            var baseDuration = TimeSpan.FromMilliseconds(100);
            var policy = CachePolicy.Default.With(duration: baseDuration, slidingExpiration: sliding);
            var cache = new TestCache(new PolicyProvider(policy));

            var parameters = new Dictionary<string, string> { { "id", "1" } };
            cache.Set("detail", parameters, new Payload { Id = "one" });

            // Wait within base duration, then hit to extend by sliding
            Thread.Sleep(70);
            Assert.NotNull(cache.Get<Payload>("detail", parameters));

            // Original expiry would have been ~100ms; with sliding it should survive past that.
            Thread.Sleep(60);
            Assert.NotNull(cache.Get<Payload>("detail", parameters));
        }

        [Fact]
        public void Scope_Component_Varies_Cache_Key_When_Present()
        {
            var policy = CachePolicy.Default.With(varyByScope: true);
            var cache = new TestCache(new PolicyProvider(policy));

            var p1 = new Dictionary<string, string> { { "q", "beatles" }, { "scope", "user:1" } };
            var p2 = new Dictionary<string, string> { { "q", "beatles" }, { "scope", "user:2" } };

            cache.Set("search", p1, new Payload { Id = "one" });
            cache.Set("search", p2, new Payload { Id = "two" });

            Assert.Equal("one", cache.Get<Payload>("search", p1)?.Id);
            Assert.Equal("two", cache.Get<Payload>("search", p2)?.Id);
        }
    }
}
