using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class RedirectSafeFollowTests
    {
        private sealed class Redirect301Handler : DelegatingHandler
        {
            public int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                if (Calls == 1)
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Moved);
                    resp.Headers.Location = new Uri("/final", UriKind.Relative);
                    return Task.FromResult(resp);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }

        private sealed class Redirect302Handler : DelegatingHandler
        {
            public int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                // Always return 302; caller decides whether to follow
                var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
                resp.Headers.Location = new Uri("/final", UriKind.Relative);
                return Task.FromResult(resp);
            }
        }

        [Fact]
        public async Task Redirect_301_Follows_When_GET()
        {
            var handler = new Redirect301Handler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://redirect.test/") };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/start");

            using var response = await client.ExecuteWithResilienceAsync(
                request,
                maxRetries: 0,
                retryBudget: TimeSpan.FromSeconds(1),
                maxConcurrencyPerHost: 2,
                perRequestTimeout: null,
                cancellationToken: CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task Redirect_302_DoesNotFollow_When_POST()
        {
            var handler = new Redirect302Handler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://redirect.test/") };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/start");
            request.Content = new StringContent("body");

            using var response = await client.ExecuteWithResilienceAsync(
                request,
                maxRetries: 0,
                retryBudget: TimeSpan.FromSeconds(1),
                maxConcurrencyPerHost: 2,
                perRequestTimeout: null,
                cancellationToken: CancellationToken.None);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(1, handler.Calls);
        }
    }
}

