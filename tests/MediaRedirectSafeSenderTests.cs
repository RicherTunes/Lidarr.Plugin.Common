using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// R2-01: media downloads must keep the SSRF policy in force across redirects. A public CDN that redirects
    /// to a private/loopback/metadata host must be refused — both when the client surfaces the 3xx (auto-redirect
    /// off, validated before the next hop) and when the client auto-follows internally (final RequestUri validated
    /// before the body is consumed).
    /// </summary>
    public sealed class MediaRedirectSafeSenderTests
    {
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _steps;
            public readonly List<Uri> Sent = new();
            public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) => _steps = new(steps);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Sent.Add(request.RequestUri!);
                var resp = _steps.Dequeue()(request);
                resp.RequestMessage ??= request;
                return Task.FromResult(resp);
            }
        }

        private static HttpResponseMessage Redirect(string location)
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found); // 302
            r.Headers.Location = new Uri(location);
            return r;
        }

        private static HttpResponseMessage Ok(string? finalUri = null)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            if (finalUri != null) r.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri);
            return r;
        }

        private static Task<HttpResponseMessage> Send(ScriptedHandler h, string url)
        {
            using var client = new HttpClient(h);
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            return MediaRedirectSafeSender.SendValidatedAsync(client, req, RemoteMediaUriPolicy.Strict);
        }

        [Fact]
        public async Task Redirect_ToPrivateHost_IsRefused_WithoutFetchingIt()
        {
            var handler = new ScriptedHandler(_ => Redirect("https://10.0.0.1/internal"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => Send(handler, "https://8.8.8.8/seg.m4s"));

            Assert.Single(handler.Sent); // only the original public host was contacted; the private target was NOT
            Assert.Equal("8.8.8.8", handler.Sent[0].Host);
        }

        [Fact]
        public async Task Redirect_ToMetadataHost_IsRefused()
        {
            var handler = new ScriptedHandler(_ => Redirect("https://169.254.169.254/latest/meta-data/"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Send(handler, "https://8.8.8.8/seg.m4s"));
        }

        [Fact]
        public async Task AutoRedirectLandedOnPrivate_FinalUriRefused()
        {
            // Simulate a client that auto-followed to a private host: 200 with RequestMessage.RequestUri private.
            var handler = new ScriptedHandler(_ => Ok(finalUri: "https://10.0.0.1/internal"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Send(handler, "https://8.8.8.8/seg.m4s"));
        }

        [Fact]
        public async Task PublicRedirect_IsFollowed_AndReturned()
        {
            var handler = new ScriptedHandler(
                _ => Redirect("https://1.1.1.1/cdn/seg.m4s"),
                _ => Ok(finalUri: "https://1.1.1.1/cdn/seg.m4s"));

            var resp = await Send(handler, "https://8.8.8.8/seg.m4s");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(2, handler.Sent.Count);
            Assert.Equal("1.1.1.1", handler.Sent[1].Host);
        }

        [Fact]
        public async Task PublicDirect_Returned()
        {
            var handler = new ScriptedHandler(_ => Ok(finalUri: "https://8.8.8.8/seg.m4s"));
            var resp = await Send(handler, "https://8.8.8.8/seg.m4s");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }
}
