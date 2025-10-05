using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Deduplication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class RequestDeduplicatorCoalesceTests
    {
        [Fact]
        public async Task GetOrCreateAsync_CoalescesConcurrentFactories()
        {
            using var deduper = new RequestDeduplicator(NullLogger<RequestDeduplicator>.Instance,
                requestTimeout: TimeSpan.FromSeconds(5), cleanupInterval: TimeSpan.FromSeconds(2));

            var key = "coalesce:test";
            var calls = 0;

            async Task<int> Factory()
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(50);
                return 42;
            }

            var t1 = deduper.GetOrCreateAsync(key, Factory);
            var t2 = deduper.GetOrCreateAsync(key, Factory);

            var r1 = await t1;
            var r2 = await t2;

            Assert.Equal(42, r1);
            Assert.Equal(42, r2);
            Assert.Equal(1, calls);
        }
    }
}

