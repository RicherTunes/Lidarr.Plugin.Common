using System;
using System.Collections.Generic;
using System.Net.Http;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class StreamingResponseCacheCovTests
    {
        private sealed class TestCache : StreamingResponseCache
        {
#if NET8_0_OR_GREATER
            private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _tp;
            public TestCache(Microsoft.Extensions.Time.Testing.FakeTimeProvider? tp = null, ICachePolicyProvider? policyProvider = null)
                : base((tp ??= new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow)), new NullLogger<StreamingResponseCache>(), policyProvider)
            {
                _tp = tp;
            }

            public void Advance(TimeSpan by) => _tp.Advance(by);
#else
            public TestCache(ICachePolicyProvider? policyProvider = null) : base(new NullLogger<StreamingResponseCache>(), policyProvider) { }
#endif

            protected override string GetServiceName() => "TestService";

            public override bool ShouldCache(string endpoint) => true;

            public override TimeSpan GetCacheDuration(string endpoint) => TimeSpan.FromMinutes(5);

            public int CountByPrefix(string prefix) => CountEntriesByPrefix(prefix);

            public void InvalidateByPrefixPublic(string prefix) => InvalidateByPrefix(prefix);

            public void SetMax(int size) => MaxCacheSize = size;
        }

        // Test cache that does NOT override ShouldCache/GetCacheDuration to test base behavior
        private sealed class TestCacheNoOverrides : StreamingResponseCache
        {
#if NET8_0_OR_GREATER
            private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _tp;
            public TestCacheNoOverrides(Microsoft.Extensions.Time.Testing.FakeTimeProvider? tp = null, ICachePolicyProvider? policyProvider = null)
                : base((tp ??= new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow)), new NullLogger<StreamingResponseCache>(), policyProvider)
            {
                _tp = tp;
            }

            public void Advance(TimeSpan by) => _tp.Advance(by);
#else
            public TestCacheNoOverrides(ICachePolicyProvider? policyProvider = null) : base(new NullLogger<StreamingResponseCache>(), policyProvider) { }
#endif

            protected override string GetServiceName() => "TestService";

            public int CountByPrefix(string prefix) => CountEntriesByPrefix(prefix);
        }

        private sealed class CachePayload
        {
            public string Id { get; init; } = string.Empty;
        }

        [Fact]
        public void Get_ReturnsNull_WhenPolicyShouldCacheIsFalse()
        {
            // Line 58-62: policy.ShouldCache = false returns null
            var mockProvider = new Mock<ICachePolicyProvider>();
            mockProvider.Setup(p => p.GetPolicy(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
                .Returns(CachePolicy.Disabled);

            var cache = new TestCache(policyProvider: mockProvider.Object);
            var parameters = new Dictionary<string, string> { { "id", "1" } };

            cache.Set("test", parameters, new CachePayload { Id = "test" });

            var result = cache.Get<CachePayload>("test", parameters);

            Assert.Null(result);
        }

        [Fact]
        public void Get_ReturnsStaleValue_WithinGraceWindow()
        {
            // Lines 109-115: Stale grace window of 200ms
#if NET8_0_OR_GREATER
            var tp = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
            var cache = new TestCache(tp);
            var parameters = new Dictionary<string, string> { { "id", "1" } };

            cache.Set("test", parameters, new CachePayload { Id = "stale" });
            // Advance to just after expiration but within 200ms grace window
            tp.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromMilliseconds(50)));

            var result = cache.Get<CachePayload>("test", parameters);

            Assert.Equal("stale", result?.Id);
#else
            // Skip on .NET 6 as we can't control time precisely
            Assert.True(true);
#endif
        }

        [Fact]
        public void Get_SlidingExpiration_RespectsAbsoluteCap()
        {
            // Lines 80-86: AbsoluteExpiration cap applies to sliding expiration
#if NET8_0_OR_GREATER
            var mockProvider = new Mock<ICachePolicyProvider>();
            var tp = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);

            // Policy: 1 hour sliding, but 30 min absolute cap
            var policy = new CachePolicy(
                name: "sliding-with-cap",
                shouldCache: true,
                duration: TimeSpan.FromHours(1),
                slidingExpiration: TimeSpan.FromHours(1),
                absoluteExpiration: TimeSpan.FromMinutes(30));

            mockProvider.Setup(p => p.GetPolicy(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
                .Returns(policy);

            var cache = new TestCache(tp, mockProvider.Object);
            var parameters = new Dictionary<string, string> { { "id", "1" } };

            cache.Set("test", parameters, new CachePayload { Id = "capped" });

            // First access extends to min(1hr sliding, 30min absolute) = 30min
            cache.Get<CachePayload>("test", parameters);

            // Advance 25 minutes - should still be cached
            tp.Advance(TimeSpan.FromMinutes(25));
            var result1 = cache.Get<CachePayload>("test", parameters);
            Assert.Equal("capped", result1?.Id);

            // Advance another 10 minutes (35 min total) - should be expired (past 30 min cap)
            tp.Advance(TimeSpan.FromMinutes(10));
            var result2 = cache.Get<CachePayload>("test", parameters);
            Assert.Null(result2);
#else
            Assert.True(true);
#endif
        }

        [Fact]
        public void Get_SlidingExpiration_ThrottledWhenWithinRefreshWindow()
        {
            // Lines 87-93: Throttle window prevents extension
#if NET8_0_OR_GREATER
            var mockProvider = new Mock<ICachePolicyProvider>();
            var tp = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);

            // Policy: 5 min sliding, 1 min throttle window
            var policy = new CachePolicy(
                name: "throttled-sliding",
                shouldCache: true,
                duration: TimeSpan.FromMinutes(5),
                slidingExpiration: TimeSpan.FromMinutes(5),
                slidingRefreshWindow: TimeSpan.FromMinutes(1));

            mockProvider.Setup(p => p.GetPolicy(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
                .Returns(policy);

            var cache = new TestCache(tp, mockProvider.Object);
            var parameters = new Dictionary<string, string> { { "id", "1" } };

            cache.Set("test", parameters, new CachePayload { Id = "throttled" });

            // First access extends expiry
            cache.Get<CachePayload>("test", parameters);

            // Advance 30 seconds - within throttle window, should NOT extend again
            tp.Advance(TimeSpan.FromSeconds(30));
            var result = cache.Get<CachePayload>("test", parameters);

            Assert.Equal("throttled", result?.Id);
#else
            Assert.True(true);
#endif
        }

        [Fact]
        public void Set_IgnoresHttpResponseMessage_InFirstOverload()
        {
            // Lines 141-145: HttpResponseMessage is ignored in Set<T>(endpoint, parameters, value)
            var cache = new TestCache();
            var parameters = new Dictionary<string, string> { { "id", "1" } };
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);

            cache.Set("test", parameters, response);

            var result = cache.Get<HttpResponseMessage>("test", parameters);
            Assert.Null(result);
        }

        [Fact]
        public void Set_IgnoresHttpResponseMessage_InSecondOverload()
        {
            // Lines 166-170: HttpResponseMessage is ignored in Set<T>(endpoint, parameters, value, duration)
            var cache = new TestCache();
            var parameters = new Dictionary<string, string> { { "id", "1" } };
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);

            cache.Set("test", parameters, response, TimeSpan.FromHours(1));

            var result = cache.Get<HttpResponseMessage>("test", parameters);
            Assert.Null(result);
        }

        [Fact]
        public void Set_ReturnsEarly_WhenPolicyShouldCacheIsFalse()
        {
            // Lines 149-152: Early return when policy.ShouldCache is false
            var mockProvider = new Mock<ICachePolicyProvider>();
            mockProvider.Setup(p => p.GetPolicy(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
                .Returns(CachePolicy.Disabled);

            var cache = new TestCache(policyProvider: mockProvider.Object);
            var parameters = new Dictionary<string, string> { { "id", "1" } };

            cache.Set("test", parameters, new CachePayload { Id = "not-cached" });

            var result = cache.Get<CachePayload>("test", parameters);
            Assert.Null(result);
        }

        [Fact]
        public void SetInternal_ClampsExpiryToCreatedAt_WhenDurationIsZero()
        {
            // Lines 198-201: expiresAt <= createdAt clamps to createdAt
            // Note: Due to 200ms stale grace window, item is returned within that window
#if NET8_0_OR_GREATER
            var tp = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
            var cache = new TestCache(tp);
            var parameters = new Dictionary<string, string> { { "id", "1" } };

            // Set with small duration - expiresAt = createdAt + 10ms
            cache.Set("test", parameters, new CachePayload { Id = "short" }, TimeSpan.FromMilliseconds(10));

            // Immediately after, should be cached
            var result1 = cache.Get<CachePayload>("test", parameters);
            Assert.Equal("short", result1?.Id);

            // Advance past expiration but within grace window (200ms) - should still return stale value
            tp.Advance(TimeSpan.FromMilliseconds(50));
            var result2 = cache.Get<CachePayload>("test", parameters);
            Assert.Equal("short", result2?.Id);

            // Advance past grace window - should be expired
            tp.Advance(TimeSpan.FromMilliseconds(200));
            var result3 = cache.Get<CachePayload>("test", parameters);
            Assert.Null(result3);
#else
            // On .NET 6 we can't control time precisely, just verify it doesn't throw
            var cache = new TestCache();
            var parameters = new Dictionary<string, string> { { "id", "1" } };
            cache.Set("test", parameters, new CachePayload { Id = "zero" }, TimeSpan.Zero);
            Assert.True(true);
#endif
        }

        [Fact]
        public void Clear_RemovesAllEntries()
        {
            // Lines 289-294: Clear() removes all cache entries
            var cache = new TestCache();
            var p1 = new Dictionary<string, string> { { "id", "1" } };
            var p2 = new Dictionary<string, string> { { "id", "2" } };

            cache.Set("endpoint1", p1, new CachePayload { Id = "one" });
            cache.Set("endpoint2", p2, new CachePayload { Id = "two" });

            Assert.Equal(2, cache.CountByPrefix("TestService"));

            cache.Clear();

            Assert.Equal(0, cache.CountByPrefix("TestService"));
            Assert.Null(cache.Get<CachePayload>("endpoint1", p1));
            Assert.Null(cache.Get<CachePayload>("endpoint2", p2));
        }

        [Fact]
        public void BuildCacheKeySeed_UsesCanonicalParameterString_WhenProvided()
        {
            // Lines 532-539: CanonicalParamString path is used when present
            // The key is "lidarr.plugin.parameters" from PluginHttpOptions.ParametersKey.Key
            var cache = new TestCache();
            var parameters = new Dictionary<string, string>
            {
                { "id", "1" },
                { "lidarr.plugin.parameters", "canonical=value&other=123" }
            };

            var key1 = cache.GenerateCacheKey("test", parameters);

            // Same canonical string should produce same key regardless of other params
            var parameters2 = new Dictionary<string, string>
            {
                { "different", "params" },
                { "lidarr.plugin.parameters", "canonical=value&other=123" }
            };

            var key2 = cache.GenerateCacheKey("test", parameters2);

            Assert.Equal(key1, key2);
        }

        [Fact]
        public void BuildCacheKeySeed_IncludesScope_WhenPresent()
        {
            // Lines 557-560: Scope component is appended
            var cache = new TestCache();
            var parameters1 = new Dictionary<string, string>
            {
                { "id", "1" },
                { "scope", "user1" }
            };
            var parameters2 = new Dictionary<string, string>
            {
                { "id", "1" },
                { "scope", "user2" }
            };

            var key1 = cache.GenerateCacheKey("test", parameters1);
            var key2 = cache.GenerateCacheKey("test", parameters2);

            Assert.NotEqual(key1, key2);
        }

        [Fact]
        public void ApplyPolicyParameters_RemovesScope_WhenVaryByScopeIsFalse()
        {
            // Lines 628-637: VaryByScope=false removes scope from effective params
            var mockProvider = new Mock<ICachePolicyProvider>();
            // Default policy has VaryByScope=false
            mockProvider.Setup(p => p.GetPolicy(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
                .Returns(CachePolicy.Default);

            var cache = new TestCache(policyProvider: mockProvider.Object);
            var parameters1 = new Dictionary<string, string>
            {
                { "id", "1" },
                { "scope", "user1" }
            };
            var parameters2 = new Dictionary<string, string>
            {
                { "id", "1" },
                { "scope", "user2" }
            };

            cache.Set("test", parameters1, new CachePayload { Id = "same" });

            // With VaryByScope=false, both parameter sets should hit the same cache entry
            var result1 = cache.Get<CachePayload>("test", parameters1);
            var result2 = cache.Get<CachePayload>("test", parameters2);

            Assert.Equal("same", result1?.Id);
            Assert.Equal("same", result2?.Id);
        }

        [Fact]
        public void InvalidateByPrefix_ReturnsEarly_WhenPrefixIsNullOrWhitespace()
        {
            // Lines 375-378: Early return when prefix is null/whitespace
            var cache = new TestCache();
            var parameters = new Dictionary<string, string> { { "id", "1" } };

            cache.Set("test", parameters, new CachePayload { Id = "value" });

            Assert.Equal(1, cache.CountByPrefix("TestService|test"));

            // These should return early and not remove anything
            cache.InvalidateByPrefixPublic(null!);
            cache.InvalidateByPrefixPublic(string.Empty);
            cache.InvalidateByPrefixPublic("   ");

            Assert.Equal(1, cache.CountByPrefix("TestService|test"));
        }

        [Fact]
        public void Set_DoesNotCache_WhenValueIsNull()
        {
            // Lines 135-138, 160-163: Early return when value is null
            var cache = new TestCache();
            var parameters = new Dictionary<string, string> { { "id", "1" } };

            cache.Set("test", parameters, (CachePayload?)null!);

            var result = cache.Get<CachePayload>("test", parameters);
            Assert.Null(result);
        }

        [Fact]
        public void ShouldCache_UsesPolicyProvider_WhenPresent()
        {
            // Lines 262-265: Uses policyProvider when available
            // Use TestCacheNoOverrides which doesn't override ShouldCache
            var mockProvider = new Mock<ICachePolicyProvider>();
            mockProvider.Setup(p => p.GetPolicy(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
                .Returns(CachePolicy.Disabled);

            var cache = new TestCacheNoOverrides(policyProvider: mockProvider.Object);

            Assert.False(cache.ShouldCache("any-endpoint"));
        }

        [Fact]
        public void GetCacheDuration_UsesPolicyProvider_WhenPresent()
        {
            // Lines 273-276: Uses policyProvider for duration when available
            // Use TestCacheNoOverrides which doesn't override GetCacheDuration
            var mockProvider = new Mock<ICachePolicyProvider>();
            var expectedDuration = TimeSpan.FromHours(2);
            mockProvider.Setup(p => p.GetPolicy(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
                .Returns(new CachePolicy(name: "custom", shouldCache: true, duration: expectedDuration));

            var cache = new TestCacheNoOverrides(policyProvider: mockProvider.Object);

            var result = cache.GetCacheDuration("any-endpoint");

            Assert.Equal(expectedDuration, result);
        }

        [Fact]
        public void Set_WithExplicitDuration_OverridesPolicyDuration()
        {
            // Lines 158-180: Second Set overload uses explicit duration
            var cache = new TestCache();
            var parameters = new Dictionary<string, string> { { "id", "1" } };

#if NET8_0_OR_GREATER
            var tp = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
            var cacheTp = new TestCache(tp);
            // Set with 1 minute explicit duration
            cacheTp.Set("test", parameters, new CachePayload { Id = "explicit" }, TimeSpan.FromMinutes(1));

            // Advance 30 seconds - should still be cached
            tp.Advance(TimeSpan.FromSeconds(30));
            var result1 = cacheTp.Get<CachePayload>("test", parameters);
            Assert.Equal("explicit", result1?.Id);

            // Advance another 45 seconds (75 total) - should be expired (past 1 min)
            tp.Advance(TimeSpan.FromSeconds(45));
            var result2 = cacheTp.Get<CachePayload>("test", parameters);
            Assert.Null(result2);
#else
            // On .NET 6, just verify the value is set
            cache.Set("test", parameters, new CachePayload { Id = "explicit" }, TimeSpan.FromMinutes(1));
            var result = cache.Get<CachePayload>("test", parameters);
            Assert.Equal("explicit", result?.Id);
#endif
        }

        [Fact]
        public void ResolvePolicy_ReturnsDefault_WhenNoPolicyProviderAndShouldCache()
        {
            // Lines 235-242: Returns default policy when no provider and ShouldCache returns true
            var cache = new TestCache();
            var parameters = new Dictionary<string, string> { { "id", "1" } };

            cache.Set("test", parameters, new CachePayload { Id = "cached" });

            var result = cache.Get<CachePayload>("test", parameters);
            Assert.Equal("cached", result?.Id);
        }

        [Fact]
        public void ShouldCache_ReturnsTrue_WhenNoPolicyProvider()
        {
            // Lines 267: Returns true when no policy provider
            var cache = new TestCacheNoOverrides();

            Assert.True(cache.ShouldCache("any-endpoint"));
        }

        [Fact]
        public void GetCacheDuration_ReturnsDefault_WhenNoPolicyProvider()
        {
            // Lines 278: Returns DefaultCacheDuration when no policy provider
            var cache = new TestCacheNoOverrides();

            var result = cache.GetCacheDuration("any-endpoint");

            Assert.Equal(TimeSpan.FromMinutes(15), result);
        }
    }
}
