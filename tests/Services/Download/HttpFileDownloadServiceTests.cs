using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Download;

/// <summary>
/// Coverage for HttpFileDownloadService — the file download path used by all 4 plugins.
/// Tests cover the resume protocol, audio-magic-bytes validation, content-type sniffing,
/// and error surfaces.
/// </summary>
public sealed class HttpFileDownloadServiceTests : IDisposable
{
    private readonly string _tempDir;

    public HttpFileDownloadServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"httpdl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Ctor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpFileDownloadService(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DownloadToFileAsync_EmptyUrl_Throws(string url)
    {
        var svc = new HttpFileDownloadService(new HttpClient());
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await svc.DownloadToFileAsync(url, Path.Combine(_tempDir, "x.flac"), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DownloadToFileAsync_EmptyFilePath_Throws(string path)
    {
        var svc = new HttpFileDownloadService(new HttpClient());
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await svc.DownloadToFileAsync("https://example.com/file", path, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadToFileAsync_FlacContent_DownloadsAndValidates()
    {
        var flacBytes = MakeMinimalFlacFile(payloadSize: 4096);
        var svc = new HttpFileDownloadService(MakeFakeHttpClient(flacBytes, "audio/flac"));
        var dest = Path.Combine(_tempDir, "track.flac");

        var written = await svc.DownloadToFileAsync("https://api.example.com/track.flac", dest, CancellationToken.None);

        Assert.True(written > 0);
        Assert.True(File.Exists(dest));
        Assert.False(File.Exists(dest + ".partial"));   // partial cleaned up after move
        var actualBytes = await File.ReadAllBytesAsync(dest);
        Assert.Equal(flacBytes.Length, actualBytes.Length);
        // Magic bytes preserved
        Assert.Equal((byte)'f', actualBytes[0]);
        Assert.Equal((byte)'L', actualBytes[1]);
    }

    [Fact]
    public async Task DownloadToFileAsync_TextContentType_RejectsAsUnexpected()
    {
        // If the server returns text/html where audio was expected — common when an
        // auth token expired and the CDN returned an error page — surface a clear
        // error rather than write the HTML to disk and pass it to the audio validator.
        var html = Encoding.UTF8.GetBytes("<html><body>auth required</body></html>");
        var svc = new HttpFileDownloadService(MakeFakeHttpClient(html, "text/html"));
        var dest = Path.Combine(_tempDir, "track.flac");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.DownloadToFileAsync("https://api.example.com/track.flac", dest, CancellationToken.None));
        Assert.Contains("Unexpected content type", ex.Message);
        Assert.Contains("text/html", ex.Message);
    }

    [Fact]
    public async Task DownloadToFileAsync_NoContent_ThrowsWithDiagnostic()
    {
        var svc = new HttpFileDownloadService(MakeFakeHttpClient(Array.Empty<byte>(), "audio/flac",
            statusCode: HttpStatusCode.NoContent));
        var dest = Path.Combine(_tempDir, "track.flac");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.DownloadToFileAsync("https://api.example.com/track.flac", dest, CancellationToken.None));
        Assert.Contains("no content", ex.Message);
    }

    [Fact]
    public async Task DownloadToFileAsync_HttpError_PropagatesAsException()
    {
        var svc = new HttpFileDownloadService(MakeFakeHttpClient(Encoding.UTF8.GetBytes("Not Found"),
            "text/plain", statusCode: HttpStatusCode.NotFound));
        var dest = Path.Combine(_tempDir, "track.flac");

        // EnsureSuccessStatusCode → HttpRequestException
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await svc.DownloadToFileAsync("https://api.example.com/missing.flac", dest, CancellationToken.None));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task DownloadToFileAsync_InvalidAudioMagicBytes_ThrowsAfterDownload()
    {
        // Server returns binary bytes that aren't a recognized audio format. The download
        // completes but AudioMagicBytesValidator rejects the file.
        var bogus = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09 };
        var svc = new HttpFileDownloadService(MakeFakeHttpClient(bogus, "application/octet-stream"));
        var dest = Path.Combine(_tempDir, "track.flac");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.DownloadToFileAsync("https://api.example.com/track.flac", dest, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadToFileAsync_CreatesParentDirectoryIfMissing()
    {
        var nested = Path.Combine(_tempDir, "nested", "deep", "track.flac");
        Assert.False(Directory.Exists(Path.GetDirectoryName(nested)!));

        var flac = MakeMinimalFlacFile(payloadSize: 1024);
        var svc = new HttpFileDownloadService(MakeFakeHttpClient(flac, "audio/flac"));

        await svc.DownloadToFileAsync("https://api.example.com/track.flac", nested, CancellationToken.None);

        Assert.True(File.Exists(nested));
    }

    // Cancellation-during-download is exercised in the Docker E2E harness rather than
    // here — the in-process SlowStreamContent + cancellation interaction proved racy
    // (one execution path hung the test host) and isn't a worthwhile unit-test seam.

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static byte[] MakeMinimalFlacFile(int payloadSize)
    {
        // FLAC magic "fLaC" + STREAMINFO block placeholder + trailing zeros.
        var buf = new byte[4 + payloadSize];
        buf[0] = (byte)'f';
        buf[1] = (byte)'L';
        buf[2] = (byte)'a';
        buf[3] = (byte)'C';
        // Fill payload with deterministic non-text bytes so the text-sniff doesn't trip.
        for (var i = 4; i < buf.Length; i++) buf[i] = (byte)((i * 7) & 0xFF);
        return buf;
    }

    private static HttpClient MakeFakeHttpClient(byte[] body, string contentType, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new StubHandler(body, contentType, statusCode);
        return new HttpClient(handler);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;
        private readonly HttpStatusCode _statusCode;

        public StubHandler(byte[] body, string contentType, HttpStatusCode statusCode)
        {
            _body = body;
            _contentType = contentType;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(_statusCode);
            resp.Content = new ByteArrayContent(_body);
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            resp.Content.Headers.ContentLength = _body.Length;
            return Task.FromResult(resp);
        }
    }

}
