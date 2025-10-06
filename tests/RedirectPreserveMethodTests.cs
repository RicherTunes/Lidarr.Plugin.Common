using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class RedirectPreserveMethodTests
    {
        private sealed class RedirectHandler : DelegatingHandler
        {
            private int _count;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _count);
                if (_count == 1)
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                    resp.Headers.Location = new Uri("/final", UriKind.Relative);
                    return Task.FromResult(resp);
                }

                // On second request, assert method and body are preserved
                Assert.Equal(HttpMethod.Post, request.Method);
                var body = (request.Content ?? new StringContent(string.Empty)).ReadAsByteArrayAsync().GetAwaiter().GetResult();
                var s = Encoding.UTF8.GetString(body);
                Assert.Equal("hello", s);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }

        private sealed class Redirect308Handler : DelegatingHandler
        {
            private int _count;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _count);
                if (_count == 1)
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.PermanentRedirect);
                    resp.Headers.Location = new Uri("/final2", UriKind.Relative);
                    return Task.FromResult(resp);
                }

                Assert.Equal(HttpMethod.Put, request.Method);
                var body = (request.Content ?? new StringContent(string.Empty)).ReadAsByteArrayAsync().GetAwaiter().GetResult();
                var s = Encoding.UTF8.GetString(body);
                Assert.Equal("payload", s);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }

        [Fact]
        public async Task Redirect_307_Preserves_Method_And_Body()
        {
            using var client = new HttpClient(new RedirectHandler())
            {
                BaseAddress = new Uri("https://example.test/")
            };
            var req = new HttpRequestMessage(HttpMethod.Post, "/start")
            {
                Content = new StringContent("hello", Encoding.UTF8, "text/plain")
            };
            using var resp = await client.ExecuteWithResilienceAsync(req, ResiliencePolicy.Default, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task Redirect_308_Preserves_Method_And_Body()
        {
            using var client = new HttpClient(new Redirect308Handler())
            {
                BaseAddress = new Uri("https://example.test/")
            };
            var req = new HttpRequestMessage(HttpMethod.Put, "/start2")
            {
                Content = new StringContent("payload", Encoding.UTF8, "text/plain")
            };
            using var resp = await client.ExecuteWithResilienceAsync(req, ResiliencePolicy.Default, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }
}
