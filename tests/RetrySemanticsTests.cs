using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Resilience;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class RetrySemanticsTests
    {
        private sealed class CountingHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
            public int Calls;

            public CountingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                return await _handler(request, cancellationToken);
            }
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_PrefersRetryAfterDate_AndClampsToBudget()
        {
            var date = DateTimeOffset.UtcNow.AddMilliseconds(400);
            var handler = new CountingHandler((req, ct) =>
            {
                if (Volatile.Read(ref _attempt) == 0)
                {
                    Volatile.Write(ref _attempt, 1);
                    var resp = new HttpResponseMessage((HttpStatusCode)429);
                    // Set date and a large delta; extension should prefer date
                    resp.Headers.RetryAfter = new RetryConditionHeaderValue(date);
                    // Add a large delta via raw header to simulate conflicting provider behavior
                    resp.Headers.TryAddWithoutValidation("Retry-After", "5");
                    return Task.FromResult(resp);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });

            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://retry.test/resource");

            var sw = Stopwatch.StartNew();
            using var response = await client.ExecuteWithResilienceAsync(
                request,
                maxRetries: 2,
                retryBudget: TimeSpan.FromSeconds(1),
                maxConcurrencyPerHost: 2,
                perRequestTimeout: null,
                cancellationToken: CancellationToken.None);
            sw.Stop();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Allow a little slack on slower CI runners
            Assert.InRange(sw.ElapsedMilliseconds, 250, 1600); // waited roughly for date (clamped by ~1s budget)
        }

        private static int _attempt = 0;

        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.NotFound)]
        public async Task ExecuteWithResilienceAsync_DoesNotRetry_Non4294xx(HttpStatusCode code)
        {
            var handler = new CountingHandler((req, ct) => Task.FromResult(new HttpResponseMessage(code)));
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://noretry.test/resource");

            using var response = await client.ExecuteWithResilienceAsync(
                request,
                maxRetries: 3,
                retryBudget: TimeSpan.FromSeconds(1),
                maxConcurrencyPerHost: 2,
                perRequestTimeout: null,
                cancellationToken: CancellationToken.None);

            Assert.Equal(code, response.StatusCode);
            Assert.Equal(1, handler.Calls);
        }
    }
}
