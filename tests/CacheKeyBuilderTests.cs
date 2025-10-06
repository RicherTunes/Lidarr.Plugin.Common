using System;
using System.Collections.Generic;
using System.Net.Http;
using Lidarr.Plugin.Common.Services.Caching;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public sealed class CacheKeyBuilderTests
    {
        [Fact]
        public void Build_IsStable_GivenSameAuthorityPathQueryAndMethod()
        {
            var uri1 = new Uri("https://api.test/search?a=1&b=2");
            var uri2 = new Uri("https://api.test/search?b=2&a=1");
            var k1 = CacheKeyBuilder.Build(HttpMethod.Get, uri1, "a=1&b=2", null);
            var k2 = CacheKeyBuilder.Build(HttpMethod.Get, uri2, "a=1&b=2", null);
            Assert.Equal(k1, k2);
        }

        [Fact]
        public void Build_Varies_By_Method_And_Scope_And_Authority()
        {
            var uA = new Uri("https://api.test/search?q=beatles");
            var uB = new Uri("https://api2.test/search?q=beatles");
            var k1 = CacheKeyBuilder.Build(HttpMethod.Get, uA, "q=beatles", "user:1");
            var k2 = CacheKeyBuilder.Build(HttpMethod.Get, uA, "q=beatles", "user:2");
            var k3 = CacheKeyBuilder.Build(HttpMethod.Post, uA, "q=beatles", "user:1");
            var k4 = CacheKeyBuilder.Build(HttpMethod.Get, uB, "q=beatles", "user:1");
            Assert.NotEqual(k1, k2); // scope varies key
            Assert.NotEqual(k1, k3); // method varies key
            Assert.NotEqual(k1, k4); // authority varies key
            Assert.Equal(k1, k1.ToLowerInvariant()); // hex lowercase
        }
    }
}
