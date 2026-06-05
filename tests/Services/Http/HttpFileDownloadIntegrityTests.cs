using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Http;

/// <summary>
/// COM-011: HttpFileDownloadService partial-download integrity checks.
///
/// After all resume cycles complete, the assembled file's byte count must equal
/// the expected total (from Content-Length or Content-Range). A mismatch throws
/// <see cref="DownloadIntegrityException"/> and deletes the partial file so the
/// next call can retry from scratch.
/// </summary>
public sealed class HttpFileDownloadIntegrityTests : IDisposable
{
    private readonly string _tempDir;

    public HttpFileDownloadIntegrityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dlintegrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ──────────────────────────────────────────────────────────────────────
    // FAILING BEFORE FIX: truncated response must throw + delete partial
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_TruncatedResponse_ThrowsAndDeletesPartial()
    {
        // Server advertises 1000 bytes but only sends 500 → integrity failure.
        var body = MakeFlacPayload(500);
        var handler = new TruncatingHandler(
            body: body,
            advertisedLength: 1000,
            contentType: "audio/flac");

        var svc = new HttpFileDownloadService(new HttpClient(handler));
        var dest = Path.Combine(_tempDir, "track.flac");

        var ex = await Assert.ThrowsAsync<DownloadIntegrityException>(async () =>
            await svc.DownloadToFileAsync("https://93.184.216.34/track.flac", dest, CancellationToken.None));

        Assert.Contains("500", ex.Message);       // actual bytes mentioned
        Assert.Contains("1000", ex.Message);      // expected bytes mentioned

        // Both the final file and the .partial file must be cleaned up.
        Assert.False(File.Exists(dest), "Final file must not exist after integrity failure.");
        Assert.False(File.Exists(dest + ".partial"), ".partial file must be deleted after integrity failure.");
    }

    [Fact]
    public async Task Download_TruncatedResponse_ExceptionHasCorrectProperties()
    {
        var body = MakeFlacPayload(200);
        var handler = new TruncatingHandler(body: body, advertisedLength: 1000, contentType: "audio/flac");

        var svc = new HttpFileDownloadService(new HttpClient(handler));
        var dest = Path.Combine(_tempDir, "truncated.flac");

        var ex = await Assert.ThrowsAsync<DownloadIntegrityException>(async () =>
            await svc.DownloadToFileAsync("https://93.184.216.34/truncated.flac", dest, CancellationToken.None));

        Assert.Equal(200L, ex.ActualBytes);
        Assert.Equal(1000L, ex.ExpectedBytes);
    }

    [Fact]
    public async Task Download_ResumedTruncation_ThrowsAfterFinalAssembly()
    {
        // Simulate a pre-existing .partial file (as if a previous download was interrupted)
        // followed by a resumed response that is itself short.
        // Expected total = existing (300) + this chunk's Content-Range total (1000) = 1000 bytes.
        // Actual chunk returned = 200 bytes → total assembled = 300 + 200 = 500 < 1000.

        var dest = Path.Combine(_tempDir, "resume-truncated.flac");
        var partialPath = dest + ".partial";

        // Write a pre-existing partial file with valid FLAC magic (300 bytes).
        var existingPartial = MakeFlacPayload(300);
        await File.WriteAllBytesAsync(partialPath, existingPartial);

        // The handler returns 206 Partial Content: 200 bytes with Content-Range total=1000.
        var resumeChunk = MakeNonTextPayload(200);
        var handler = new PartialContentHandler(
            chunkBody: resumeChunk,
            totalLength: 1000,
            contentType: "audio/flac");

        var svc = new HttpFileDownloadService(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<DownloadIntegrityException>(async () =>
            await svc.DownloadToFileAsync("https://93.184.216.34/track.flac", dest, CancellationToken.None));

        // 300 (existing) + 200 (chunk) = 500; expected = 1000
        Assert.Equal(500L, ex.ActualBytes);
        Assert.Equal(1000L, ex.ExpectedBytes);

        Assert.False(File.Exists(dest), "Final file must not exist after integrity failure.");
        Assert.False(File.Exists(partialPath), ".partial file must be deleted after integrity failure.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // MUST PASS: exact byte count = success
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_ExactByteCount_Succeeds()
    {
        var body = MakeFlacPayload(4096);
        var handler = new ExactLengthHandler(body: body, contentType: "audio/flac");

        var svc = new HttpFileDownloadService(new HttpClient(handler));
        var dest = Path.Combine(_tempDir, "exact.flac");

        var written = await svc.DownloadToFileAsync("https://93.184.216.34/exact.flac", dest, CancellationToken.None);

        Assert.Equal((long)body.Length, written);
        Assert.True(File.Exists(dest));
        Assert.False(File.Exists(dest + ".partial"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // No Content-Length: chunked transfer — must not throw (limitation documented)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_NoContentLength_ChunkedTransferEncoding_CompletesWithoutIntegrityError()
    {
        // When the server sends no Content-Length header (chunked transfer encoding),
        // we have no expected total to verify against. The download must succeed and
        // NOT throw DownloadIntegrityException. CRC/hash verification is out of scope
        // for this check (requires server-side manifest). This test documents the limitation.
        var body = MakeFlacPayload(2048);
        var handler = new ChunkedNoLengthHandler(body: body, contentType: "audio/flac");

        var svc = new HttpFileDownloadService(new HttpClient(handler));
        var dest = Path.Combine(_tempDir, "chunked.flac");

        // Must NOT throw DownloadIntegrityException.
        var written = await svc.DownloadToFileAsync("https://93.184.216.34/chunked.flac", dest, CancellationToken.None);

        Assert.Equal((long)body.Length, written);
        Assert.True(File.Exists(dest));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>Creates a minimal valid FLAC byte array of <paramref name="size"/> bytes.</summary>
    private static byte[] MakeFlacPayload(int size)
    {
        var buf = new byte[Math.Max(size, 8)];
        buf[0] = (byte)'f';
        buf[1] = (byte)'L';
        buf[2] = (byte)'a';
        buf[3] = (byte)'C';
        // Fill remaining with non-text bytes to avoid the text-content-type guard.
        for (var i = 4; i < buf.Length; i++) buf[i] = (byte)((i * 7 + 13) & 0xFF);
        return buf[..size];
    }

    /// <summary>Creates a non-text binary payload (no FLAC magic — used for resume chunks).</summary>
    private static byte[] MakeNonTextPayload(int size)
    {
        var buf = new byte[size];
        for (var i = 0; i < size; i++) buf[i] = (byte)((i * 11 + 97) & 0xFF);
        return buf;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Fake HTTP handlers
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the given body but advertises a larger Content-Length
    /// so the download will be short by (advertisedLength - body.Length) bytes.
    /// </summary>
    private sealed class TruncatingHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly long _advertisedLength;
        private readonly string _contentType;

        public TruncatingHandler(byte[] body, long advertisedLength, string contentType)
        {
            _body = body;
            _advertisedLength = advertisedLength;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body)
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            // Lie: tell the client to expect more bytes than we'll actually send.
            resp.Content.Headers.ContentLength = _advertisedLength;
            return Task.FromResult(resp);
        }
    }

    /// <summary>
    /// Returns 206 Partial Content for a resume request, with the specified chunk body
    /// and Content-Range header indicating <paramref name="totalLength"/> total bytes.
    /// </summary>
    private sealed class PartialContentHandler : HttpMessageHandler
    {
        private readonly byte[] _chunkBody;
        private readonly long _totalLength;
        private readonly string _contentType;

        public PartialContentHandler(byte[] chunkBody, long totalLength, string contentType)
        {
            _chunkBody = chunkBody;
            _totalLength = totalLength;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Pretend to honor the Range request from the existing partial file.
            // Find the requested start byte.
            long start = 0;
            if (request.Headers.Range != null)
            {
                var enumerator = request.Headers.Range.Ranges.GetEnumerator();
                if (enumerator.MoveNext())
                    start = enumerator.Current.From ?? 0;
            }

            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(_chunkBody)
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            resp.Content.Headers.ContentLength = _chunkBody.Length;
            // Content-Range: bytes start-(start+chunk-1)/total
            resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                start, start + _chunkBody.Length - 1, _totalLength);
            return Task.FromResult(resp);
        }
    }

    /// <summary>Returns the exact body with a matching Content-Length.</summary>
    private sealed class ExactLengthHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;

        public ExactLengthHandler(byte[] body, string contentType)
        {
            _body = body;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body)
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            resp.Content.Headers.ContentLength = _body.Length;
            return Task.FromResult(resp);
        }
    }

    /// <summary>
    /// Returns the body without a Content-Length header (simulates chunked transfer).
    /// The HttpClient transport will set Transfer-Encoding: chunked implicitly.
    /// </summary>
    private sealed class ChunkedNoLengthHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _contentType;

        public ChunkedNoLengthHandler(byte[] body, string contentType)
        {
            _body = body;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body)
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            // Deliberately omit ContentLength to simulate chunked transfer encoding.
            return Task.FromResult(resp);
        }
    }
}
