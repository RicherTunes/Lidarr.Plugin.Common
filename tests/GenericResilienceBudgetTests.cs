using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class GenericResilienceBudgetTests
    {
        [Theory]
        [InlineData("budget-backward.test", 60000000L, -36000000000L, 1)]
        [InlineData("budget-forward.test", 10000000L, 36000000000L, 2)]
        [InlineData("budget-equality.test", 50000000L, 50000000L, 2)]
        [InlineData("budget-one-tick-over.test", 50000001L, 50000001L, 1)]
        public async Task Should_UseMonotonicBudget_ForZeroDelayRetry(
            string host, long elapsedTicks, long utcChangeTicks, int expectedSends)
        {
            var clock = new IndependentTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var policy = ResiliencePolicy.Default.With(maxRetries: 2, retryBudget: TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/resource");
            using var original = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("retry later")
            };
            using var success = new HttpResponseMessage(HttpStatusCode.OK);
            var sends = 0;

            using var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                (_, _) =>
                {
                    sends++;
                    if (sends == 1)
                    {
                        clock.Timestamp += elapsedTicks;
                        clock.UtcNow = clock.UtcNow.AddTicks(utcChangeTicks);
                        return Task.FromResult(original);
                    }

                    return Task.FromResult(success);
                },
                r => Task.FromResult(r),
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                _ => TimeSpan.Zero,
                policy,
                clock).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(expectedSends, sends);
            if (expectedSends == 1)
            {
                Assert.Same(original, response);
                Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
                Assert.Equal("retry later", await response.Content.ReadAsStringAsync());
            }
            else
            {
                Assert.Same(success, response);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        [Fact]
        public async Task Should_RefuseZeroDelayRetry_WhenCustomFrequencyExceedsBudget()
        {
            var utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var clock = new IndependentTimeProvider(utcNow, frequency: 1000);
            var policy = ResiliencePolicy.Default.With(maxRetries: 2, retryBudget: TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://budget-frequency.test/resource");
            using var original = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("retry later")
            };
            using var success = new HttpResponseMessage(HttpStatusCode.OK);
            var sends = 0;

            using var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                (_, _) =>
                {
                    sends++;
                    if (sends == 1)
                    {
                        clock.Timestamp += 6000;
                        return Task.FromResult(original);
                    }

                    return Task.FromResult(success);
                },
                r => Task.FromResult(r),
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                _ => TimeSpan.Zero,
                policy,
                clock);

            Assert.Equal(utcNow, clock.UtcNow);
            Assert.Equal(1, sends);
            Assert.Same(original, response);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("retry later", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Should_ReturnSuccess_WithMaxValueRetryBudget()
        {
            await AssertSuccessfulFirstResponseAsync(
                "budget-max-value.test",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                TimeSpan.MaxValue);
        }

        [Fact]
        public async Task Should_ReturnSuccess_WhenUtcIsNearMaxValue()
        {
            await AssertSuccessfulFirstResponseAsync(
                "budget-utc-max.test",
                DateTimeOffset.MaxValue.AddSeconds(-1),
                TimeSpan.FromSeconds(5));
        }

        private static async Task AssertSuccessfulFirstResponseAsync(string host, DateTimeOffset utcNow, TimeSpan budget)
        {
            var clock = new IndependentTimeProvider(utcNow);
            var policy = ResiliencePolicy.Default.With(maxRetries: 2, retryBudget: budget);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/resource");
            using var success = new HttpResponseMessage(HttpStatusCode.OK);
            var sends = 0;

            using var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                (_, _) =>
                {
                    sends++;
                    return Task.FromResult(success);
                },
                r => Task.FromResult(r),
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                _ => TimeSpan.Zero,
                policy,
                clock).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, sends);
            Assert.Same(success, response);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        private sealed class IndependentTimeProvider : TimeProvider
        {
            private readonly long _frequency;

            public IndependentTimeProvider(DateTimeOffset utcNow, long frequency = TimeSpan.TicksPerSecond)
            {
                UtcNow = utcNow;
                _frequency = frequency;
            }

            public DateTimeOffset UtcNow { get; set; }
            public long Timestamp { get; set; }
            public override long TimestampFrequency => _frequency;
            public override DateTimeOffset GetUtcNow() => UtcNow;
            public override long GetTimestamp() => Timestamp;
        }
    }
}
