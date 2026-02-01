using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Http;
using Moq;
using Moq.Protected;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Integration")]
    public class RateLimitTelemetryHandlerTests
    {
        #region Test Setup

        private class MockRateLimitObserver : IRateLimitObserver
        {
            public TimeSpan? LastDelay { get; private set; }
            public DateTimeOffset? LastTimestamp { get; private set; }
            public int CallCount { get; private set; }

            public void RecordRetryAfter(TimeSpan delay, DateTimeOffset timestamp)
            {
                LastDelay = delay;
                LastTimestamp = timestamp;
                CallCount++;
            }
        }

        private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
        {
            var response = new HttpResponseMessage(statusCode);

            if (retryAfter.HasValue)
            {
                if (retryAfter.Value < TimeSpan.FromDays(1))
                {
                    // Use delta for shorter delays
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
                }
                else
                {
                    // Use date for longer delays
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.Add(retryAfter.Value));
                }
            }

            return response;
        }

        #endregion

        #region 429 Response Tests

        [Fact]
        public async Task SendAsync_Records429WithRetryAfter()
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var delay = TimeSpan.FromSeconds(60);

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(CreateResponse(HttpStatusCode.TooManyRequests, delay));

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(1, observer.CallCount);
            Assert.Equal(delay, observer.LastDelay);
        }

        [Fact]
        public async Task SendAsync_Records429WithZeroRetryAfter()
        {
            // Arrange
            var observer = new MockRateLimitObserver();

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(CreateResponse(HttpStatusCode.TooManyRequests, TimeSpan.Zero));

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(1, observer.CallCount);
            Assert.Equal(TimeSpan.Zero, observer.LastDelay);
        }

        [Fact]
        public async Task SendAsync_Records429WithoutRetryAfter()
        {
            // Arrange
            var observer = new MockRateLimitObserver();

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.TooManyRequests)); // No Retry-After header

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            // Should not record when Retry-After is missing
            Assert.Equal(0, observer.CallCount);
        }

        #endregion

        #region 503 Response Tests

        [Fact]
        public async Task SendAsync_Records503WithRetryAfter()
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var delay = TimeSpan.FromSeconds(120);

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(CreateResponse(HttpStatusCode.ServiceUnavailable, delay));

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(1, observer.CallCount);
            Assert.Equal(delay, observer.LastDelay);
        }

        [Fact]
        public async Task SendAsync_Records503WithoutRetryAfter()
        {
            // Arrange
            var observer = new MockRateLimitObserver();

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(0, observer.CallCount);
        }

        #endregion

        #region Non-Rate-Limit Response Tests

        [Fact]
        public async Task SendAsync_IgnoresNonRateLimitResponses()
        {
            // Arrange
            var observer = new MockRateLimitObserver();

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, observer.CallCount);
            Assert.Null(observer.LastDelay);
        }

        [Fact]
        public async Task SendAsync_Ignores404NotFound()
        {
            // Arrange
            var observer = new MockRateLimitObserver();

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(0, observer.CallCount);
        }

        [Fact]
        public async Task SendAsync_Ignores500InternalServerError()
        {
            // Arrange
            var observer = new MockRateLimitObserver();

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(0, observer.CallCount);
        }

        #endregion

        #region Retry-After Delta Calculation Tests

        [Fact]
        public async Task SendAsync_CalculatesDelayFromDelta()
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var expectedDelay = TimeSpan.FromSeconds(30);

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(CreateResponse(HttpStatusCode.TooManyRequests, expectedDelay));

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            await client.SendAsync(request);

            // Assert
            Assert.Equal(expectedDelay, observer.LastDelay);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(10)]
        [InlineData(60)]
        [InlineData(300)]
        [InlineData(3600)]
        public async Task SendAsync_HandlesVariousDeltaDelays(int seconds)
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var expectedDelay = TimeSpan.FromSeconds(seconds);

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(CreateResponse(HttpStatusCode.TooManyRequests, expectedDelay));

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            await client.SendAsync(request);

            // Assert
            Assert.Equal(expectedDelay, observer.LastDelay);
        }

        #endregion

        #region Retry-After Date Calculation Tests

        [Fact]
        public async Task SendAsync_CalculatesDelayFromDate()
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var futureTime = DateTimeOffset.UtcNow.AddHours(1);

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(futureTime);

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            await client.SendAsync(request);

            // Assert
            Assert.NotNull(observer.LastDelay);
            // Delay should be approximately 1 hour (within reasonable margin)
            Assert.True(observer.LastDelay.Value.TotalMinutes > 58);
            Assert.True(observer.LastDelay.Value.TotalMinutes < 62);
        }

        [Fact]
        public async Task SendAsync_HandlesPastRetryAfterDate()
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var pastTime = DateTimeOffset.UtcNow.AddHours(-1); // Past time

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(pastTime);

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            await client.SendAsync(request);

            // Assert - Should normalize to TimeSpan.Zero for past dates
            Assert.Equal(TimeSpan.Zero, observer.LastDelay);
        }

        #endregion

        #region Timestamp Tests

        [Fact]
        public async Task SendAsync_PassesTimestampToObserver()
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var beforeTime = DateTimeOffset.UtcNow;

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(CreateResponse(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(60)));

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            await client.SendAsync(request);
            var afterTime = DateTimeOffset.UtcNow;

            // Assert
            Assert.NotNull(observer.LastTimestamp);
            Assert.True(observer.LastTimestamp >= beforeTime);
            Assert.True(observer.LastTimestamp <= afterTime);
        }

        #endregion

        #region Null Observer Tests

        [Fact]
        public void Constructor_NullObserver_ThrowsArgumentNullException()
        {
            // Arrange
            var mockHandler = new Mock<HttpMessageHandler>();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
            {
#if NET8_0_OR_GREATER
                new RateLimitTelemetryHandler(null!, TimeProvider.System, mockHandler.Object);
#else
                new RateLimitTelemetryHandler(null!, mockHandler.Object);
#endif
            });
        }

        #endregion

        #region Multiple Requests Tests

        [Fact]
        public async Task SendAsync_HandlesMultipleRequestsSequentially()
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var requestCount = 0;

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(() =>
                {
                    requestCount++;
                    // Every other request returns 429
                    if (requestCount % 2 == 0)
                    {
                        return Task.FromResult(CreateResponse(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30)));
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                });

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);

            // Act - Send 4 requests
            for (int i = 0; i < 4; i++)
            {
                await client.GetAsync("https://example.com");
            }

            // Assert - 2 rate limits should have been recorded
            Assert.Equal(2, observer.CallCount);
        }

        #endregion

        #region Request/Response Pass-Through Tests

        [Fact]
        public async Task SendAsync_PassesThroughOriginalResponse()
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var originalResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("test content")
            };

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(originalResponse);

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("test content", await response.Content.ReadAsStringAsync());
        }

        #endregion

        #region Status Code Edge Cases Tests

        [Theory]
        [InlineData(429)] // Too Many Requests
        [InlineData(503)] // Service Unavailable
        public async Task SendAsync_HandlesRateLimitStatusCodes(int statusCode)
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var response = new HttpResponseMessage((HttpStatusCode)statusCode);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            await client.SendAsync(request);

            // Assert
            Assert.Equal(1, observer.CallCount);
            Assert.Equal(TimeSpan.FromSeconds(60), observer.LastDelay);
        }

        #endregion

        #region Cancellation Tests

        [Fact]
        public async Task SendAsync_PropagatesCancellation()
        {
            // Arrange
            var observer = new MockRateLimitObserver();
            var cts = new CancellationTokenSource();

            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(async () =>
                {
                    await Task.Delay(1000, cts.Token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                });

#if NET8_0_OR_GREATER
            using var handler = new RateLimitTelemetryHandler(observer, TimeProvider.System, mockHandler.Object);
#else
            using var handler = new RateLimitTelemetryHandler(observer, mockHandler.Object);
#endif
            using var client = new HttpClient(handler);
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

            // Act
            var task = client.SendAsync(request, cts.Token);
            cts.Cancel();

            // Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        #endregion
    }
}
