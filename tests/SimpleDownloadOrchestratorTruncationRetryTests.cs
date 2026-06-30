using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Regression tests for ecosystem-wide download robustness: a mid-body HTTP truncation
    /// (System.Net.Http.HttpIOException "the response ended prematurely / ResponseEnded", or a
    /// transport reset) thrown DURING the body copy — after ExecuteWithResilienceAsync already
    /// returned the headers — must be retried with a Range-resume rather than failing the track.
    /// Mirrors the live-proven Qobuz TrackDownloadService fix at the Common level so tidal/amazon/
    /// apple benefit and the qobuz consolidation onto this orchestrator does not regress.
    /// </summary>
    public sealed class SimpleDownloadOrchestratorTruncationRetryTests
    {
        // Overrides the backoff to zero so retries are instantaneous in tests.
        private sealed class FastRetryOrchestrator : SimpleDownloadOrchestrator
        {
            public FastRetryOrchestrator(HttpClient http, int maxAttempts)
                : base(
                    serviceName: "Test",
                    httpClient: http,
                    getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                    getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                    getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                    getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")))
            {
                MaxDownloadAttempts = maxAttempts;
            }

            internal override int MaxDownloadAttempts { get; }
            internal override TimeSpan GetRetryDelay(int attempt) => TimeSpan.Zero;
        }

        // A stream that yields `throwAfter` bytes (from a deterministic pattern starting at `offset`)
        // then throws to simulate a server-side mid-body truncation.
        private sealed class TruncatingStream : Stream
        {
            private readonly int _offset;
            private readonly int _throwAfter;
            private int _pos;

            public TruncatingStream(int offset, int throwAfter) { _offset = offset; _throwAfter = throwAfter; }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_pos >= _throwAfter)
                {
                    throw new IOException("simulated truncation: the response ended prematurely");
                }
                int toCopy = Math.Min(buffer.Length, _throwAfter - _pos);
                for (int i = 0; i < toCopy; i++) buffer.Span[i] = (byte)((_offset + _pos + i) % 251);
                _pos += toCopy;
                return ValueTask.FromResult(toCopy);
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _pos; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // Truncates on attempt #1 (delivering `truncateAfter` bytes then throwing); on the Range-resume
        // attempt(s) it serves the remaining bytes as 206 to completion.
        private sealed class TruncateOnceThenResumeHandler : HttpMessageHandler
        {
            private readonly int _total;
            private readonly int _truncateAfter;
            public int Calls;

            public TruncateOnceThenResumeHandler(int total, int truncateAfter) { _total = total; _truncateAfter = truncateAfter; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                int start = 0;
                if (request.Headers.Range != null)
                {
                    foreach (var r in request.Headers.Range.Ranges) { start = (int)(r.From ?? 0); break; }
                }

                if (Calls == 1)
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new TruncatingStream(0, _truncateAfter))
                    };
                    resp.Content.Headers.ContentLength = _total; // declares full length, but the body cuts short
                    return Task.FromResult(resp);
                }

                var remaining = _total - start;
                var ok = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(BuildBytes(start, remaining))
                };
                ok.Content.Headers.ContentLength = remaining;
                return Task.FromResult(ok);
            }
        }

        // Always truncates immediately (0 bytes), so the download never completes.
        private sealed class AlwaysTruncateHandler : HttpMessageHandler
        {
            public int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new TruncatingStream(0, 0))
                };
                resp.Content.Headers.ContentLength = 100_000;
                return Task.FromResult(resp);
            }
        }

        private sealed class TimeoutStream : Stream
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => throw new TaskCanceledException("simulated body timeout");

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class BodyTimeoutOnceThenSuccessHandler : HttpMessageHandler
        {
            private readonly int _total;
            public int Calls;

            public BodyTimeoutOnceThenSuccessHandler(int total)
            {
                _total = total;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                if (Calls == 1)
                {
                    var timeout = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new TimeoutStream())
                    };
                    timeout.Content.Headers.ContentLength = _total;
                    return Task.FromResult(timeout);
                }

                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(BuildBytes(0, _total))
                };
                ok.Content.Headers.ContentLength = _total;
                return Task.FromResult(ok);
            }
        }

        private static byte[] BuildBytes(int offset, int count)
        {
            var d = new byte[count];
            for (int i = 0; i < count; i++) d[i] = (byte)((offset + i) % 251);
            return d;
        }

        private static void Cleanup(string temp)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            try { if (File.Exists(temp + ".partial")) File.Delete(temp + ".partial"); } catch { }
            try { if (File.Exists(temp + ".partial.resume.json")) File.Delete(temp + ".partial.resume.json"); } catch { }
        }

        [Fact]
        public async Task DownloadTrack_resumes_after_midbody_truncation_and_completes()
        {
            const int total = 200_000;
            var handler = new TruncateOnceThenResumeHandler(total, truncateAfter: 60_000);
            using var http = new HttpClient(handler);
            var orch = new FastRetryOrchestrator(http, maxAttempts: 4);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_trunc_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);

                Assert.True(result.Success, $"Download should have resumed and succeeded; error: {result.ErrorMessage}");
                Assert.True(File.Exists(result.FilePath));
                Assert.Equal(total, new FileInfo(result.FilePath).Length);
                Assert.True(handler.Calls >= 2, $"Expected a resume retry (>=2 HTTP calls), got {handler.Calls}");
            }
            finally { Cleanup(temp); Cleanup(Path.ChangeExtension(temp, "bin")); }
        }

        [Fact]
        public async Task DownloadTrack_retries_body_timeout_when_caller_token_not_cancelled()
        {
            const int total = 64_000;
            var handler = new BodyTimeoutOnceThenSuccessHandler(total);
            using var http = new HttpClient(handler);
            var orch = new FastRetryOrchestrator(http, maxAttempts: 3);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_timeout_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);

                Assert.True(result.Success, $"Download should retry a provider timeout; error: {result.ErrorMessage}");
                Assert.True(File.Exists(result.FilePath));
                Assert.Equal(total, new FileInfo(result.FilePath).Length);
                Assert.Equal(2, handler.Calls);
            }
            finally { Cleanup(temp); Cleanup(Path.ChangeExtension(temp, "bin")); }
        }

        [Fact]
        public async Task DownloadTrack_fails_after_exhausting_retries_on_persistent_truncation()
        {
            var handler = new AlwaysTruncateHandler();
            using var http = new HttpClient(handler);
            var orch = new FastRetryOrchestrator(http, maxAttempts: 3);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_trunc_fail_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);

                Assert.False(result.Success, "A persistently truncating source must ultimately fail, not hang or falsely succeed");
                Assert.Equal(3, handler.Calls);
            }
            finally { Cleanup(temp); Cleanup(Path.ChangeExtension(temp, "bin")); }
        }
    }
}
