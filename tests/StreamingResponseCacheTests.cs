using System;
using System.Collections.Generic;
using System.Threading;
using Lidarr.Plugin.Common.Services.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class StreamingResponseCacheTests
    {
        private sealed class TestCache : StreamingResponseCache
        {
            public TestCache() : base(new NullLogger<StreamingResponseCache>()) { }

            protected override string GetServiceName() => "TestService";

            public override bool ShouldCache(string endpoint) => true;

            public override TimeSpan GetCacheDuration(string endpoint) => TimeSpan.FromMinutes(5);

            public int CountByPrefix(string prefix) => CountEntriesByPrefix(prefix);

            public void InvalidateByPrefixPublic(string prefix) => InvalidateByPrefix(prefix);

            public void SetMax(int size) => MaxCacheSize = size;
        }

        private sealed class CachePayload
        {
            public string Id { get; init; } = string.Empty;
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

        [Fact]
        public void ClearEndpoint_RemovesEntriesForEndpoint()
        {
            var cache = new TestCache();
            var endpointAParams1 = new Dictionary<string, string> { { "id", "1" } };
            var endpointAParams2 = new Dictionary<string, string> { { "id", "2" } };
            var endpointBParams = new Dictionary<string, string> { { "id", "3" } };

            cache.Set("catalog/search", endpointAParams1, new CachePayload { Id = "A1" });
            cache.Set("catalog/search", endpointAParams2, new CachePayload { Id = "A2" });
            cache.Set("catalog/detail", endpointBParams, new CachePayload { Id = "B" });

            Assert.NotNull(cache.Get<CachePayload>("catalog/search", endpointAParams1));
            Assert.NotNull(cache.Get<CachePayload>("catalog/search", endpointAParams2));
            Assert.NotNull(cache.Get<CachePayload>("catalog/detail", endpointBParams));

            cache.ClearEndpoint("catalog/search");

            Assert.Null(cache.Get<CachePayload>("catalog/search", endpointAParams1));
            Assert.Null(cache.Get<CachePayload>("catalog/search", endpointAParams2));
            Assert.NotNull(cache.Get<CachePayload>("catalog/detail", endpointBParams));
        }

        [Fact]
        public void Set_EnforcesMaxCacheSizeByEvictingOldest()
        {
            var cache = new TestCache();
            cache.SetMax(2);

            var p1 = new Dictionary<string, string> { { "id", "1" } };
            var p2 = new Dictionary<string, string> { { "id", "2" } };
            var p3 = new Dictionary<string, string> { { "id", "3" } };

            cache.Set("catalog/detail", p1, new CachePayload { Id = "one" });
            Thread.Sleep(5);
            cache.Set("catalog/detail", p2, new CachePayload { Id = "two" });
            Thread.Sleep(5);
            cache.Set("catalog/detail", p3, new CachePayload { Id = "three" });

            Assert.Null(cache.Get<CachePayload>("catalog/detail", p1));
            Assert.NotNull(cache.Get<CachePayload>("catalog/detail", p2));
            Assert.NotNull(cache.Get<CachePayload>("catalog/detail", p3));
            Assert.Equal(2, cache.CountByPrefix("TestService|catalog/detail"));
        }

        [Fact]
        public void InvalidateByPrefix_UsesCacheSeedWhenMatching()
        {
            var cache = new TestCache();
            var searchParams1 = new Dictionary<string, string> { { "q", "beatles" } };
            var searchParams2 = new Dictionary<string, string> { { "q", "stones" } };
            var otherParams = new Dictionary<string, string> { { "id", "99" } };

            cache.Set("search", searchParams1, new CachePayload { Id = "1" });
            cache.Set("search", searchParams2, new CachePayload { Id = "2" });
            cache.Set("lookup", otherParams, new CachePayload { Id = "3" });

            Assert.Equal(2, cache.CountByPrefix("TestService|search"));

            cache.InvalidateByPrefixPublic("TestService|search");

            Assert.Null(cache.Get<CachePayload>("search", searchParams1));
            Assert.Null(cache.Get<CachePayload>("search", searchParams2));
            Assert.NotNull(cache.Get<CachePayload>("lookup", otherParams));
        }
    }
}


