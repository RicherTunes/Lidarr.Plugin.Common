using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class GenericResilienceExecutorTests
    {
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
                cancellationToken: CancellationToken.None);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(1, attempts);
        }

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
                await Task.Delay(50, ct).ConfigureAwait(false);
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
                    cancellationToken: CancellationToken.None);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            Assert.True(peak >= 5, $"Expected peak concurrency >=5 but was {peak}");
        }
    }
}
