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

            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = async (req, ct) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    var r = new HttpResponseMessage((HttpStatusCode)429);
                    r.Headers.Add("Retry-After", "1");
                    return r;
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            };

            Func<HttpRequestMessage, Task<HttpRequestMessage>> clone = async (r) =>
            {
                var c = new HttpRequestMessage(r.Method, r.RequestUri);
                foreach (var h in r.Headers)
                    c.Headers.TryAddWithoutValidation(h.Key, h.Value);
                return c;
            };

            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                request,
                send,
                clone,
                r => r.RequestUri?.Host,
                r => (int)r.StatusCode,
                r =>
                {
                    if (r.Headers.RetryAfter?.Delta.HasValue == true) return r.Headers.RetryAfter!.Delta;
                    return null;
                },
                maxRetries: 3,
                retryBudget: TimeSpan.FromSeconds(10),
                maxConcurrencyPerHost: 2,
                cancellationToken: CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, attempts);
        }
    }
}

