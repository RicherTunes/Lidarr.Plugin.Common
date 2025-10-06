using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public sealed class QueryCanonicalizerTests
    {
        [Fact]
        public void Canonicalize_OrdersKeysAndValues_AndEncodes()
        {
            var pairs = new List<KeyValuePair<string, string>>
            {
                new("b", "2"),
                new("a", "1"),
                new("a", "10"),
                new("space", "a b"),
            };

            var canon = QueryCanonicalizer.Canonicalize(pairs);
            Assert.Equal("a=1%2c10&b=2&space=a%20b", canon);
        }

        [Fact]
        public void Canonicalize_IsOrderInsensitive()
        {
            var p1 = new List<KeyValuePair<string, string>> { new("q", "beatles"), new("page", "2") };
            var p2 = new List<KeyValuePair<string, string>> { new("page", "2"), new("q", "beatles") };

            Assert.Equal(QueryCanonicalizer.Canonicalize(p1), QueryCanonicalizer.Canonicalize(p2));
        }
    }
}
