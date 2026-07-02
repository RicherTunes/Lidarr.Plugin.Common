using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Services.Performance;
using Moq;
using Moq.Protected;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class AdaptiveRateLimitingHandlerTests
    {
        /// <summary>
        /// Regression (harden campaign): a 429 carrying a far-future Retry-After Date produced a
        /// delay beyond Task.Delay's ~49.7-day limit, throwing ArgumentOutOfRangeException that
        /// escaped the OperationCanceledException-only catch — turning a benign 429 into a hard
        /// request failure. The delay is now clamped; SendAsync must return the 429, not throw.
        /// </summary>
        [Fact]
        public async Task SendAsync_429_FarFutureRetryAfterDate_DoesNotThrow_ReturnsResponse()
        {
            var limiter = new Mock<IUniversalAdaptiveRateLimiter>();
            limiter.Setup(l => l.WaitIfNeededAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);

            var resp429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp429.Headers.RetryAfter = new RetryConditionHeaderValue(
                new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero)); // ~8000 years out

            var inner = new Mock<HttpMessageHandler>();
            inner.Protected()
                 .Setup<Task<HttpResponseMessage>>(
                     "SendAsync",
                     ItExpr.IsAny<HttpRequestMessage>(),
                     ItExpr.IsAny<CancellationToken>())
                 .ReturnsAsync(resp429);

            // maxRetryAfterDelay: Zero clamps the (far-future) honour-wait to 0 so the handler
            // skips Task.Delay and returns immediately — keeping the test fast and deterministic.
            using var handler = new AdaptiveRateLimitingHandler(
                limiter.Object, "TestSvc", logger: null, maxRetryAfterDelay: TimeSpan.Zero)
            {
                InnerHandler = inner.Object
            };
            using var client = new HttpClient(handler);

            var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "https://example.com/v1/x"), CancellationToken.None);

            // Pre-fix the unclamped far-future delay threw ArgumentOutOfRangeException from Task.Delay.
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        [Fact]
        public async Task SendAsync_UsesHostFirstSegmentEndpointKeyForAllLimiterInteractions()
        {
            const string service = "Tidal";
            const string expectedEndpoint = "api.tidal.com:v1";

            var limiter = new Mock<IUniversalAdaptiveRateLimiter>(MockBehavior.Strict);
            limiter.Setup(l => l.WaitIfNeededAsync(service, expectedEndpoint, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
            limiter.Setup(l => l.GetCurrentLimit(service, expectedEndpoint))
                   .Returns(300);
            limiter.Setup(l => l.RecordResponse(service, expectedEndpoint, It.IsAny<HttpResponseMessage>()));
            limiter.Setup(l => l.Dispose());

            var inner = new Mock<HttpMessageHandler>();
            inner.Protected()
                 .Setup<Task<HttpResponseMessage>>(
                     "SendAsync",
                     ItExpr.IsAny<HttpRequestMessage>(),
                     ItExpr.IsAny<CancellationToken>())
                 .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            using var handler = new AdaptiveRateLimitingHandler(limiter.Object, service)
            {
                InnerHandler = inner.Object
            };
            using var client = new HttpClient(handler);

            using var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "https://api.tidal.com/v1/search?q=album"),
                CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            limiter.Verify(
                l => l.WaitIfNeededAsync(service, expectedEndpoint, It.IsAny<CancellationToken>()),
                Times.Once);
            limiter.Verify(
                l => l.RecordResponse(service, expectedEndpoint, It.Is<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.OK)),
                Times.Once);
            limiter.Verify(
                l => l.GetCurrentLimit(service, expectedEndpoint),
                Times.Exactly(2));
        }
    }
}
