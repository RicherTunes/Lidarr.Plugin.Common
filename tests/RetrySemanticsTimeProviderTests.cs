using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class RetrySemanticsTimeProviderTests
    {
        private sealed class CountingHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
            public int Calls;

            public CountingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                return _handler(request, cancellationToken);
            }
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_RespectsRetryAfterAbsoluteDate_WithFakeTime()
        {
            var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
            int attempt = 0;
            var date = fake.GetUtcNow().AddMilliseconds(400);

            var handler = new CountingHandler((req, ct) =>
            {
                if (Volatile.Read(ref attempt) == 0)
                {
                    Volatile.Write(ref attempt, 1);
                    var resp = new HttpResponseMessage((HttpStatusCode)429);
                    resp.Headers.RetryAfter = new RetryConditionHeaderValue(date);
                    return Task.FromResult(resp);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });

            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://retry.example/resource");

            var task = client.ExecuteWithResilienceAsync(
                request,
                maxRetries: 2,
                retryBudget: TimeSpan.FromSeconds(2),
                maxConcurrencyPerHost: 2,
                perRequestTimeout: null,
                timeProvider: fake,
                cancellationToken: CancellationToken.None);

            // Advance time to trigger the Retry-After absolute date
            fake.Advance(TimeSpan.FromMilliseconds(400));

            using var response = await task;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, handler.Calls);
        }
    }
}
