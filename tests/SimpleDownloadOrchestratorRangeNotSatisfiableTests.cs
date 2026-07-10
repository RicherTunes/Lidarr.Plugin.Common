using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Regression tests for HTTP 416 (RequestedRangeNotSatisfiable) on a resume attempt: when a
    /// stale ".partial" makes the orchestrator send a Range the server can no longer satisfy, the
    /// 416 must NOT be retried with the SAME stale partial (which can only 416 again until the
    /// attempt budget is exhausted, failing the track → album → host re-grab loop — the exact
    /// pathology retry-with-resume exists to prevent). Instead the stale partial + resume state is
    /// discarded and the next attempt issues a clean full GET. A 416 with no partial (no Range
    /// sent) stays an ordinary bounded HTTP failure.
    /// </summary>
    public sealed class SimpleDownloadOrchestratorRangeNotSatisfiableTests
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

        // Serves 416 for ANY Range request, but a 200 full body when no Range header is present —
        // the shape of a server whose stored representation no longer matches the stale partial.
        private sealed class Reject416AnyRangeHandler : HttpMessageHandler
        {
            private readonly byte[] _payload;
            public int Calls;
            public int RangeCalls;
            public int FullCalls;

            public Reject416AnyRangeHandler(byte[] payload) { _payload = payload; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                if (request.Headers.Range is not null)
                {
                    RangeCalls++;
                    var invalid = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    };
                    invalid.Content.Headers.ContentRange = new ContentRangeHeaderValue(_payload.Length);
                    return Task.FromResult(invalid);
                }

                FullCalls++;
                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_payload)
                };
                ok.Content.Headers.ContentLength = _payload.Length;
                return Task.FromResult(ok);
            }
        }

        // Pathological server: 416 for EVERY request, Range or not.
        private sealed class Always416Handler : HttpMessageHandler
        {
            public int Calls;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                var resp = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
                return Task.FromResult(resp);
            }
        }

        private static byte[] BuildBytes(int count)
        {
            var d = new byte[count];
            for (int i = 0; i < count; i++) d[i] = (byte)(i % 251);
            return d;
        }

        private static string SeedStalePartial(string outputPath, int staleBytes)
        {
            var tempPath = outputPath + ".partial";
            var resumePath = tempPath + ".resume.json";
            File.WriteAllBytes(tempPath, new byte[staleBytes]); // junk content — deliberately stale
            File.WriteAllText(resumePath, "{\"DownloadedBytes\":" + staleBytes + ",\"TotalExpectedBytes\":0,\"ETag\":\"\\\"stale-etag\\\"\",\"LastModifiedUtc\":null}");
            return tempPath;
        }

        private static void Cleanup(string temp)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            try { if (File.Exists(temp + ".partial")) File.Delete(temp + ".partial"); } catch { }
            try { if (File.Exists(temp + ".partial.resume.json")) File.Delete(temp + ".partial.resume.json"); } catch { }
        }

        [Fact]
        public async Task DownloadTrack_416_on_resume_discards_stale_partial_and_succeeds_with_clean_full_get()
        {
            const int total = 64_000;
            var handler = new Reject416AnyRangeHandler(BuildBytes(total));
            using var http = new HttpClient(handler);
            var orch = new FastRetryOrchestrator(http, maxAttempts: 4);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_416_resume_{Guid.NewGuid():N}.bin");
            var tempPartial = SeedStalePartial(temp, staleBytes: 500);
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);

                Assert.True(result.Success, $"A 416'd resume must restart with a clean full GET and succeed; error: {result.ErrorMessage}");
                Assert.True(File.Exists(result.FilePath));
                Assert.Equal(total, new FileInfo(result.FilePath).Length);
                // The stale partial produced exactly one 416'd Range attempt, then a clean full GET.
                Assert.Equal(1, handler.RangeCalls);
                Assert.Equal(1, handler.FullCalls);
                // Stale resume state must be gone after the successful download.
                Assert.False(File.Exists(tempPartial), "stale .partial must be discarded");
                Assert.False(File.Exists(tempPartial + ".resume.json"), "stale resume.json must be discarded");
            }
            finally { Cleanup(temp); }
        }

        [Fact]
        public async Task DownloadTrack_416_forever_with_stale_partial_fails_cleanly_within_attempt_budget()
        {
            var handler = new Always416Handler();
            using var http = new HttpClient(handler);
            var orch = new FastRetryOrchestrator(http, maxAttempts: 3);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_416_forever_{Guid.NewGuid():N}.bin");
            var tempPartial = SeedStalePartial(temp, staleBytes: 500);
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);

                Assert.False(result.Success, "A server that 416s everything must fail the track, not loop forever");
                Assert.Equal(3, handler.Calls); // the clean-restart still consumes attempts — bounded by MaxDownloadAttempts
                Assert.False(File.Exists(tempPartial), "stale .partial must be discarded after the 416'd resume attempt");
            }
            finally { Cleanup(temp); }
        }

        [Fact]
        public async Task DownloadTrack_416_without_partial_is_an_ordinary_bounded_http_failure()
        {
            var handler = new Always416Handler();
            using var http = new HttpClient(handler);
            var orch = new FastRetryOrchestrator(http, maxAttempts: 3);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_416_nopartial_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);

                Assert.False(result.Success);
                Assert.Equal(3, handler.Calls);
            }
            finally { Cleanup(temp); }
        }
    }
}
