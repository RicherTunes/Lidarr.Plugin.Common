using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Caller routing for the transient-vs-unsafe distinction: a transient (DNS-resolution failure) guard result
    /// must surface as a RETRYABLE <see cref="HttpRequestException"/> (which Lidarr and the plugins'
    /// IsTransientDownloadException/retry logic already treat as retryable), while a hard security block must stay
    /// a permanent <see cref="InvalidOperationException"/>. Asserting the private-IP path stays a hard block is the
    /// security guarantee — the transient relaxation must never let a resolved-to-private target through.
    /// </summary>
    public sealed class SsrfTransientCallerTests
    {
        // A handler that fails the test if it is ever asked to send — the guard must throw BEFORE any fetch.
        private sealed class NeverSendHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => throw new Xunit.Sdk.XunitException($"HTTP send must not happen for a blocked URL: {request.RequestUri}");
        }

        private static HttpClient NeverSendClient() => new(new NeverSendHandler());

        private static RemoteMediaUriPolicy TransientDnsPolicy() =>
            new() { DnsResolver = _ => throw new SocketException(11001) };

        private static RemoteMediaUriPolicy ResolvesPrivatePolicy() =>
            new() { DnsResolver = _ => new[] { IPAddress.Parse("10.0.0.1") } };

        private const string HostnameUrl = "https://sp-ad-cf.audio.tidal.com/seg0.m4s";

        // ---- ChunkedHttpAssembler ----

        [Fact]
        public async Task ChunkedAssembler_TransientResolution_ThrowsRetryableHttpRequestException()
        {
            var assembler = new ChunkedHttpAssembler(NeverSendClient(), TransientDnsPolicy());
            var chunks = new[] { new ChunkSpec(0, HostnameUrl) };
            var outPath = Path.Combine(Path.GetTempPath(), $"lpc_test_{Guid.NewGuid():N}.bin");

            await Assert.ThrowsAsync<HttpRequestException>(() => assembler.AssembleAsync(chunks, outPath));
        }

        [Fact]
        public async Task ChunkedAssembler_ResolvesPrivate_ThrowsPermanentInvalidOperation()
        {
            var assembler = new ChunkedHttpAssembler(NeverSendClient(), ResolvesPrivatePolicy());
            var chunks = new[] { new ChunkSpec(0, HostnameUrl) };
            var outPath = Path.Combine(Path.GetTempPath(), $"lpc_test_{Guid.NewGuid():N}.bin");

            await Assert.ThrowsAsync<InvalidOperationException>(() => assembler.AssembleAsync(chunks, outPath));
        }

        // ---- HttpFileDownloadService ----

        [Fact]
        public async Task HttpFileDownload_TransientResolution_ThrowsRetryableHttpRequestException()
        {
            var svc = new HttpFileDownloadService(NeverSendClient(), TransientDnsPolicy());
            var outPath = Path.Combine(Path.GetTempPath(), $"lpc_test_{Guid.NewGuid():N}.bin");

            await Assert.ThrowsAsync<HttpRequestException>(() => svc.DownloadToFileAsync(HostnameUrl, outPath, CancellationToken.None));
        }

        [Fact]
        public async Task HttpFileDownload_ResolvesPrivate_ThrowsPermanentInvalidOperation()
        {
            var svc = new HttpFileDownloadService(NeverSendClient(), ResolvesPrivatePolicy());
            var outPath = Path.Combine(Path.GetTempPath(), $"lpc_test_{Guid.NewGuid():N}.bin");

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DownloadToFileAsync(HostnameUrl, outPath, CancellationToken.None));
        }

        // ---- MediaRedirectSafeSender ----

        [Fact]
        public async Task MediaRedirectSafeSender_TransientResolution_ThrowsRetryableHttpRequestException()
        {
            using var client = NeverSendClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, HostnameUrl);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                MediaRedirectSafeSender.SendValidatedAsync(client, req, TransientDnsPolicy()));
        }

        [Fact]
        public async Task MediaRedirectSafeSender_ResolvesPrivate_ThrowsPermanentInvalidOperation()
        {
            using var client = NeverSendClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, HostnameUrl);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                MediaRedirectSafeSender.SendValidatedAsync(client, req, ResolvesPrivatePolicy()));
        }
    }
}
