using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Performance;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Behavior-pinning tests for <see cref="NamedServiceRateLimiter"/> — the shared base
    /// that Tidalarr/Qobuzarr/AppleMusicarr subclass for their plugin-local rate limiters.
    /// Pins: service-name normalization (blank service routes to the canonical name),
    /// shortcut helpers, and the dispose contract (query methods throw, record methods
    /// become silent no-ops, the wrapped inner limiter is disposed too).
    /// </summary>
    [Trait("Category", "Unit")]
    public class NamedServiceRateLimiterTests
    {
        /// <summary>Minimal concrete subclass exposing the protected surface for assertions.</summary>
        private sealed class TestLimiter : NamedServiceRateLimiter
        {
            public TestLimiter(string serviceName, UniversalAdaptiveRateLimiter? inner = null)
                : base(serviceName, inner)
            {
            }

            public UniversalAdaptiveRateLimiter InnerForTest => Inner;
            public string ServiceNameForTest => ServiceName;
        }

        private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Constructor_RejectsBlankServiceName(string? name)
        {
            Assert.Throws<ArgumentException>(() => new TestLimiter(name!));
        }

        [Fact]
        public void Constructor_NullInner_CreatesFreshLimiter()
        {
            using var limiter = new TestLimiter("Tidal");

            Assert.NotNull(limiter.InnerForTest);
            Assert.Equal("Tidal", limiter.ServiceNameForTest);
        }

        [Fact]
        public void Constructor_SuppliedInner_IsWrappedNotReplaced()
        {
            using var inner = new UniversalAdaptiveRateLimiter();
            using var limiter = new TestLimiter("Qobuz", inner);

            Assert.Same(inner, limiter.InnerForTest);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task WaitIfNeededAsync_BlankService_RoutesToCanonicalServiceName(string? service)
        {
            using var limiter = new TestLimiter("Tidal");

            var proceeded = await limiter.WaitIfNeededAsync(service!, "search");

            Assert.True(proceeded);
            // The request must be booked under the canonical name, not the blank one.
            Assert.Equal(1, limiter.InnerForTest.GetServiceStats("Tidal").TotalRequests);
            Assert.Equal(0, limiter.InnerForTest.GetServiceStats(service ?? string.Empty).TotalRequests);
        }

        [Fact]
        public async Task WaitIfNeededAsync_ExplicitService_IsPreservedVerbatim()
        {
            using var limiter = new TestLimiter("Tidal");

            await limiter.WaitIfNeededAsync("Qobuz", "search");

            Assert.Equal(1, limiter.InnerForTest.GetServiceStats("Qobuz").TotalRequests);
            Assert.Equal(0, limiter.InnerForTest.GetServiceStats("Tidal").TotalRequests);
        }

        [Fact]
        public void RecordResponse_BlankService_RoutesToCanonicalServiceName()
        {
            using var limiter = new TestLimiter("Tidal");
            using var response = Ok();

            limiter.RecordResponse(string.Empty, "albums", response);

            Assert.True(limiter.InnerForTest.GetServiceStats("Tidal").EndpointStats.ContainsKey("albums"));
        }

        [Fact]
        public void RecordResponse_NullEndpoint_IsCoercedToEmpty_NotThrown()
        {
            using var limiter = new TestLimiter("Tidal");
            using var response = Ok();

            limiter.RecordResponse("Tidal", null!, response);

            Assert.True(limiter.InnerForTest.GetServiceStats("Tidal").EndpointStats.ContainsKey(string.Empty));
        }

        [Fact]
        public async Task ShortcutHelpers_UseCanonicalServiceName()
        {
            using var limiter = new TestLimiter("AppleMusic");
            using var response = Ok();

            await limiter.WaitIfNeededAsync("catalog");
            limiter.RecordResponse("catalog", response);

            var stats = limiter.GetNamedServiceStats();
            Assert.Equal("AppleMusic", stats.ServiceName);
            Assert.Equal(1, stats.TotalRequests);
            Assert.True(stats.EndpointStats.ContainsKey("catalog"));
        }

        [Fact]
        public void GetCurrentLimit_BlankService_UsesCanonicalConfig()
        {
            using var limiter = new TestLimiter("Tidal");

            // Blank routes to "Tidal"; both spellings must agree on the budget.
            Assert.Equal(limiter.GetCurrentLimit("Tidal", "search"), limiter.GetCurrentLimit("", "search"));
            Assert.True(limiter.GetCurrentLimit("", "search") > 0);
        }

        [Fact]
        public async Task Dispose_QueryMethodsThrow_RecordMethodsAreSilentNoOps()
        {
            var limiter = new TestLimiter("Tidal");
            using var response = Ok();
            limiter.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => limiter.WaitIfNeededAsync("Tidal", "search"));
            Assert.Throws<ObjectDisposedException>(() => limiter.GetCurrentLimit("Tidal", "search"));
            Assert.Throws<ObjectDisposedException>(() => limiter.GetServiceStats("Tidal"));
            Assert.Throws<ObjectDisposedException>(() => limiter.GetGlobalStats());

            // Record paths must never throw post-dispose (fire-and-forget callers).
            limiter.RecordResponse("Tidal", "search", response);
            limiter.RecordAuthFailure("Tidal", "search");
        }

        [Fact]
        public async Task Dispose_IsIdempotent_AndDisposesInner()
        {
            var inner = new UniversalAdaptiveRateLimiter();
            var limiter = new TestLimiter("Tidal", inner);

            limiter.Dispose();
            limiter.Dispose(); // second call must be a no-op

            // The wrapper owns the inner limiter: it must be disposed with the wrapper.
            await Assert.ThrowsAsync<ObjectDisposedException>(() => inner.WaitIfNeededAsync("Tidal", "search"));
        }

        [Fact]
        public void GetGlobalStats_AggregatesAcrossServices()
        {
            using var limiter = new TestLimiter("Tidal");
            using var response = Ok();

            limiter.RecordResponse("Tidal", "a", response);
            limiter.RecordResponse("Qobuz", "b", response);

            var global = limiter.GetGlobalStats();
            Assert.True(global.ServiceStats.ContainsKey("Tidal"));
            Assert.True(global.ServiceStats.ContainsKey("Qobuz"));
        }
    }
}
