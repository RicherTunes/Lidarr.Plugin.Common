using System;
using System.Net.Http;
using System.Net.Http.Headers;
using Lidarr.Plugin.Common.Services.Http;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class RateLimitHeaderUtilitiesTests
    {
        [Fact]
        public void ResolveRetryAfter_NullHeader_ReturnsZero()
        {
            Assert.Equal(TimeSpan.Zero, RateLimitHeaderUtilities.ResolveRetryAfter((RetryConditionHeaderValue?)null));
        }

        [Fact]
        public void ResolveRetryAfter_DeltaForm_ReturnsDelta()
        {
            var header = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            Assert.Equal(TimeSpan.FromSeconds(30), RateLimitHeaderUtilities.ResolveRetryAfter(header));
        }

        [Fact]
        public void ResolveRetryAfter_NegativeDelta_ClampedToZero()
        {
            // RetryConditionHeaderValue itself rejects negative deltas via its constructor,
            // but we still verify the utility's contract for "past dates" via the date form.
            var pastDate = DateTimeOffset.UtcNow.AddMinutes(-5);
            var header = new RetryConditionHeaderValue(pastDate);
            Assert.Equal(TimeSpan.Zero, RateLimitHeaderUtilities.ResolveRetryAfter(header));
        }

        [Fact]
        public void ResolveRetryAfter_FutureDate_ReturnsPositiveDelay()
        {
            var futureDate = DateTimeOffset.UtcNow.AddSeconds(45);
            var header = new RetryConditionHeaderValue(futureDate);
            var delay = RateLimitHeaderUtilities.ResolveRetryAfter(header);
            Assert.InRange(delay.TotalSeconds, 30, 60);
        }

        [Fact]
        public void ResolveRetryAfter_ResponseOverload_HandlesMissingHeader()
        {
            using var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            Assert.Equal(TimeSpan.Zero, RateLimitHeaderUtilities.ResolveRetryAfter(response));
        }

        [Fact]
        public void ResolveRetryAfter_ResponseOverload_NullResponse_ReturnsZero()
        {
            Assert.Equal(TimeSpan.Zero, RateLimitHeaderUtilities.ResolveRetryAfter((HttpResponseMessage?)null));
        }

        [Fact]
        public void BuildHostFirstSegmentKey_TidalSearch_ReturnsApiPlusV1()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.tidal.com/v1/search?q=foo");
            Assert.Equal("api.tidal.com:v1", RateLimitHeaderUtilities.BuildHostFirstSegmentKey(req));
        }

        [Fact]
        public void BuildHostFirstSegmentKey_QobuzApiJson_ReturnsApiJsonSegment()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.qobuz.com/api.json/0.2/album/get");
            Assert.Equal("www.qobuz.com:api.json", RateLimitHeaderUtilities.BuildHostFirstSegmentKey(req));
        }

        [Fact]
        public void BuildHostFirstSegmentKey_HostOnly_ReturnsEmptyFirstSegment()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
            Assert.Equal("example.com:", RateLimitHeaderUtilities.BuildHostFirstSegmentKey(req));
        }

        [Fact]
        public void BuildHostFirstSegmentKey_NullUri_ReturnsUnknownKey()
        {
            using var req = new HttpRequestMessage();
            Assert.Equal("unknown", RateLimitHeaderUtilities.BuildHostFirstSegmentKey(req));
        }

        [Fact]
        public void BuildHostFirstSegmentKey_CustomUnknownKey_Honored()
        {
            Assert.Equal("anonymous", RateLimitHeaderUtilities.BuildHostFirstSegmentKey((Uri?)null, "anonymous"));
        }

        [Fact]
        public void BuildHostFirstSegmentKey_NullRequest_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => RateLimitHeaderUtilities.BuildHostFirstSegmentKey((HttpRequestMessage)null!));
        }
    }
}
