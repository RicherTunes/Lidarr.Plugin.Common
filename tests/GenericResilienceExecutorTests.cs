using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class GenericResilienceExecutorTests
    {
        [Fact]
        public async Task Should_ReturnUsableOriginalResponse_WhenNegativeRetryAfterMeetsExpiredBudget()
        {
            var tp = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var policy = ResiliencePolicy.Default.With(maxRetries: 2, retryBudget: TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://negative-retry-after.test/resource");
            using var original = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("retry later")
            };
            using var success = new HttpResponseMessage(HttpStatusCode.OK);
            var attempts = 0;

            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                (_, _) =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        tp.Advance(TimeSpan.FromSeconds(6));
                        return Task.FromResult(original);
                    }

                    return Task.FromResult(success);
                },
                r => Task.FromResult(r),
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                _ => TimeSpan.FromSeconds(-10),
                policy,
                tp);

            Assert.Equal(1, attempts);
            Assert.Same(original, response);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("retry later", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Should_ReturnUsableOriginalResponse_WhenMaxValueRetryAfterExceedsBudget()
        {
            var tp = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var policy = ResiliencePolicy.Default.With(maxRetries: 2, retryBudget: TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://max-retry-after.test/resource");
            using var original = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("retry later")
            };
            var attempts = 0;

            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                (_, _) =>
                {
                    attempts++;
                    return Task.FromResult(original);
                },
                r => Task.FromResult(r),
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                _ => TimeSpan.MaxValue,
                policy,
                tp);

            Assert.Equal(1, attempts);
            Assert.Same(original, response);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("retry later", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Should_UsePolicyBackoffAndRetry_WhenRetryAfterIsAbsent()
        {
            var tp = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var policy = ResiliencePolicy.Default.With(
                maxRetries: 2,
                retryBudget: TimeSpan.FromSeconds(5),
                initialBackoff: TimeSpan.FromSeconds(2),
                maxBackoff: TimeSpan.FromSeconds(2),
                jitterMin: TimeSpan.Zero,
                jitterMax: TimeSpan.Zero);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://absent-retry-after.test/resource");
            using var original = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            using var success = new HttpResponseMessage(HttpStatusCode.OK);
            using var cts = new CancellationTokenSource();
            var attempts = 0;

            var pending = GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                (_, _) => Task.FromResult(++attempts == 1 ? original : success),
                r => Task.FromResult(r),
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                r => r.Headers.RetryAfter?.Delta,
                policy,
                tp,
                cts.Token);

            try
            {
                Assert.Equal(1, attempts);
                Assert.False(pending.IsCompleted);
                tp.Advance(TimeSpan.FromMilliseconds(1999));
                Assert.Equal(1, attempts);
                Assert.False(pending.IsCompleted);
                tp.Advance(TimeSpan.FromMilliseconds(1));

                using var response = await pending;
                Assert.Equal(2, attempts);
                Assert.Same(success, response);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            finally
            {
                cts.Cancel();
            }
        }

        [Fact]
        public async Task ExecuteWithResilience_RetriesOn429_WithRetryAfter()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://example.com/test");

            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    var r = new HttpResponseMessage((HttpStatusCode)429);
                    r.Headers.Add("Retry-After", "1");
                    return Task.FromResult(r);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            };

            Func<HttpRequestMessage, Task<HttpRequestMessage>> clone = r =>
            {
                var c = new HttpRequestMessage(r.Method, r.RequestUri);
                foreach (var h in r.Headers)
                {
                    c.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }

                return Task.FromResult(c);
            };

            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                send,
                clone,
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                r => r.Headers.RetryAfter?.Delta,
                maxRetries: 3,
                retryBudget: TimeSpan.FromSeconds(10),
                maxConcurrencyPerHost: 2,
                perRequestTimeout: null,
                cancellationToken: CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, attempts);
        }

        [Fact]
        public async Task ExecuteWithResilience_StopsOnBudgetExhaustion()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://example.com/test2");

            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                r.Headers.Add("Retry-After", "120");
                return Task.FromResult(r);
            };

            Func<HttpRequestMessage, Task<HttpRequestMessage>> clone = r => Task.FromResult(new HttpRequestMessage(r.Method, r.RequestUri));

            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                send,
                clone,
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                r => r.Headers.RetryAfter?.Delta,
                maxRetries: 5,
                retryBudget: TimeSpan.FromMilliseconds(10),
                maxConcurrencyPerHost: 1,
                perRequestTimeout: null,
                cancellationToken: CancellationToken.None);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task ExecuteWithResilience_ThrowsTimeoutExceptionWhenPerRequestTimeoutExceeded()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://timeout.test/resource");

            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = async (req, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1000), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };

            Func<HttpRequestMessage, Task<HttpRequestMessage>> clone = r => Task.FromResult(new HttpRequestMessage(r.Method, r.RequestUri));

            await Assert.ThrowsAsync<TimeoutException>(() => GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                send,
                clone,
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                _ => null,
                maxRetries: 1,
                retryBudget: TimeSpan.FromSeconds(1),
                maxConcurrencyPerHost: 1,
                perRequestTimeout: TimeSpan.FromMilliseconds(100),
                cancellationToken: CancellationToken.None));
        }
        // snippet-skip-compile
        // snippet:generic-cancel
        [Fact]
        public async Task ExecuteWithResilience_CallerCancellationPropagates()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://cancelled.test/resource");

            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = async (req, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };

            Func<HttpRequestMessage, Task<HttpRequestMessage>> clone = r => Task.FromResult(new HttpRequestMessage(r.Method, r.RequestUri));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                send,
                clone,
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                _ => null,
                maxRetries: 1,
                retryBudget: TimeSpan.FromSeconds(1),
                maxConcurrencyPerHost: 2,
                perRequestTimeout: TimeSpan.FromSeconds(5),
                cancellationToken: cts.Token));
        }
        // end-snippet



        [Fact]
        public async Task ExecuteWithResilience_IncreasesHostGateOnHigherConcurrency()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://host-gate.test/resource");
            var concurrent = 0;
            var peak = 0;

            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = async (req, ct) =>
            {
                Interlocked.Increment(ref concurrent);
                peak = Math.Max(peak, concurrent);
                await Task.Delay(50, ct);
                Interlocked.Decrement(ref concurrent);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };

            Func<HttpRequestMessage, Task<HttpRequestMessage>> clone = r => Task.FromResult(new HttpRequestMessage(r.Method, r.RequestUri));

            var tasks = new Task<HttpResponseMessage>[6];
            for (var i = 0; i < tasks.Length; i++)
            {
                tasks[i] = GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                    request,
                    send,
                    clone,
                    r => r.RequestUri?.Host,
                    r => (int)r.StatusCode,
                    r => null,
                    maxRetries: 1,
                    retryBudget: TimeSpan.FromSeconds(5),
                    maxConcurrencyPerHost: 6,
                    perRequestTimeout: null,
                    cancellationToken: CancellationToken.None);
            }

            await Task.WhenAll(tasks);
            Assert.True(peak >= 5, $"Expected peak concurrency >=5 but was {peak}");
            HostGateRegistry.Clear("host-gate.test");
        }

        [Fact]
        public async Task ExecuteWithResilience_UpgradesHostGateWhenConcurrencyIncreases()
        {
            const string host = "host-gate-upgrade.test";
            Func<HttpRequestMessage, Task<HttpRequestMessage>> clone = r =>
                Task.FromResult(new HttpRequestMessage(r.Method, r.RequestUri));

            static void UpdatePeak(ref int peak, int current)
            {
                while (true)
                {
                    var observed = peak;
                    if (current <= observed)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref peak, current, observed) == observed)
                    {
                        return;
                    }
                }
            }

            async Task<int> RunBatchAsync(int requestedLimit)
            {
                var concurrent = 0;
                var peak = 0;
                var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}/resource");

                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = async (req, ct) =>
                {
                    var current = Interlocked.Increment(ref concurrent);
                    UpdatePeak(ref peak, current);

                    try
                    {
                        await Task.Delay(40, ct);
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref concurrent);
                    }
                };

                var tasks = new Task<HttpResponseMessage>[8];
                for (var i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                        request,
                        send,
                        clone,
                        r => r.RequestUri?.Host,
                        r => (int)r.StatusCode,
                        r => null,
                        maxRetries: 1,
                        retryBudget: TimeSpan.FromSeconds(5),
                        maxConcurrencyPerHost: requestedLimit,
                        perRequestTimeout: null,
                        cancellationToken: CancellationToken.None);
                }

                await Task.WhenAll(tasks);
                return peak;
            }

            var initialPeak = await RunBatchAsync(requestedLimit: 2);
            var upgradedPeak = await RunBatchAsync(requestedLimit: 6);

            Assert.InRange(initialPeak, 1, 2);
            Assert.True(upgradedPeak > initialPeak, $"Expected upgraded peak to exceed initial peak (initial {initialPeak}, upgraded {upgradedPeak})");
            Assert.True(upgradedPeak >= 5, $"Expected upgraded peak concurrency >=5 but was {upgradedPeak}");

            HostGateRegistry.Clear(host);
        }
    }
}
