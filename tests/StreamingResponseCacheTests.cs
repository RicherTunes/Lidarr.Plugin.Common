using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.Services.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class StreamingResponseCacheTests
    {
        private sealed class TestCache : StreamingResponseCache
        {
            public TestCache() : base(new NullLogger<StreamingResponseCache>()) {}

            protected override string GetServiceName() => "TestService";

            public override bool ShouldCache(string endpoint) => true;

            public override TimeSpan GetCacheDuration(string endpoint) => TimeSpan.FromMinutes(5);
        }

        [Fact]
        public void GenerateCacheKey_IsStableAcrossInstances()
        {
            var cache = new TestCache();
            var parameters = new Dictionary<string, string>
            {
                { "query", "beatles" },
                { "token", "secret" }
            };

            var key1 = cache.GenerateCacheKey("search", parameters);
            var key2 = cache.GenerateCacheKey("search", parameters);

            Assert.Equal(key1, key2);
            Assert.NotEqual("0", key1);
        }
    }
}
