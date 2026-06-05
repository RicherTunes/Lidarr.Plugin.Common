using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Download;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// LOOP-004: ExecuteWithResilienceAsync follows 301/302/307/308 redirects itself. When a caller supplies a
    /// redirect-target validator (media callers pass the SSRF guard), a 3xx that points at an internal/unsafe
    /// host must be refused before the next hop — a hostile CDN can't bounce the resilience layer to localhost.
    /// </summary>
    public sealed class ResilienceRedirectSsrfTests
    {
        private sealed class RedirectHandler : DelegatingHandler
        {
            private readonly string _location;
            public int Calls;
            public RedirectHandler(string location) => _location = location;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                if (Calls == 1)
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Redirect); // 302
                    r.Headers.Location = new Uri(_location);
                    return Task.FromResult(r);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) });
            }
        }

        private static bool GuardAllows(Uri u) => RemoteMediaUriGuard.Validate(u, RemoteMediaUriPolicy.Strict).IsAllowed;

        [Fact]
        public async Task RedirectToPrivateHost_IsRefused_BeforeFetchingIt()
        {
            var handler = new RedirectHandler("https://10.0.0.1/internal"); // 302 → private
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://8.8.8.8/") };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/seg");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ExecuteWithResilienceAsync(
                    request, ResiliencePolicy.Streaming, CancellationToken.None,
                    validateRedirectTarget: GuardAllows));

            Assert.Equal(1, handler.Calls); // only the original public host was contacted; the private target was NOT
        }

        [Fact]
        public async Task RedirectToPublicHost_IsFollowed_WithValidator()
        {
            var handler = new RedirectHandler("https://1.1.1.1/cdn/seg"); // 302 → public
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://8.8.8.8/") };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/seg");

            using var resp = await client.ExecuteWithResilienceAsync(
                request, ResiliencePolicy.Streaming, CancellationToken.None,
                validateRedirectTarget: GuardAllows);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(2, handler.Calls); // followed the public redirect
        }

        [Fact]
        public async Task RedirectToPrivateHost_NoValidator_FollowsAsBefore()
        {
            // Without a validator the behavior is unchanged (the ~26 non-media callers): the redirect is followed.
            var handler = new RedirectHandler("https://10.0.0.1/internal");
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://8.8.8.8/") };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/seg");

            using var resp = await client.ExecuteWithResilienceAsync(request, ResiliencePolicy.Streaming, CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(2, handler.Calls);
        }
    }
}
