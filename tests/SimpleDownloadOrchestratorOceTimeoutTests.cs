using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Guards the provider/transport cancellation contract: a NON-caller OperationCanceledException
    /// (e.g. a per-request HttpClient timeout → TaskCanceledException, with the caller token NOT
    /// cancelled) must be handled as a TRACK FAILURE (retried/failed), NOT escape as a whole-album
    /// cancellation. Only a genuine caller cancellation propagates. (Regression guard for the #58
    /// over-correction that caught every OperationCanceledException unconditionally — a single-track
    /// timeout would abort the entire album for chunk-path plugins instead of failing one track.)
    /// </summary>
    public sealed class SimpleDownloadOrchestratorOceTimeoutTests
    {
        private sealed class ThrowingStreamProvider : IAudioStreamProvider
        {
            private readonly Func<CancellationToken, Exception> _ex;
            public ThrowingStreamProvider(Func<CancellationToken, Exception> ex) => _ex = ex;
            public Task<AudioStreamResult> GetStreamAsync(string trackId, StreamingQuality? quality = null, CancellationToken cancellationToken = default)
                => throw _ex(cancellationToken);
        }

        private sealed class AlwaysTimeoutHandler : HttpMessageHandler
        {
            public int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                // A per-request HttpClient timeout surfaces as TaskCanceledException with the caller's
                // token NOT cancelled — i.e., a non-caller cancellation.
                throw new TaskCanceledException("request timeout");
            }
        }

        private static SimpleDownloadOrchestrator MakeChunk(IAudioStreamProvider provider, HttpClient http) =>
            new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "bin")),
                streamProvider: provider);

        private static SimpleDownloadOrchestrator MakeUrl(HttpClient http) =>
            new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")));

        [Fact]
        public async Task ChunkProvider_NonCallerTimeout_FailsTrack_DoesNotEscapeAsCancellation()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 1, supportRange: false));
            var provider = new ThrowingStreamProvider(_ => new TaskCanceledException("provider timeout"));
            var orch = MakeChunk(provider, http);
            var dir = Path.Combine(Path.GetTempPath(), "oce-" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 }, null!, CancellationToken.None);
                Assert.False(result.Success, "a provider timeout (non-caller OCE) is a track failure, not a whole-album cancellation");
            }
            finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
        }

        [Fact]
        public async Task ChunkProvider_NonCallerOperationCanceledException_FailsTrack_DoesNotEscapeAsCancellation()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 1, supportRange: false));
            var provider = new ThrowingStreamProvider(_ => new OperationCanceledException("provider operation timed out"));
            var orch = MakeChunk(provider, http);
            var dir = Path.Combine(Path.GetTempPath(), "oce-" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 }, null!, CancellationToken.None);
                Assert.False(result.Success, "a plain non-caller OCE is a track failure, not a whole-album cancellation");
            }
            finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
        }

        [Fact]
        public async Task ChunkProvider_CallerCancellation_Propagates()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 1, supportRange: false));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var provider = new ThrowingStreamProvider(ct => new OperationCanceledException(ct));
            var orch = MakeChunk(provider, http);
            var dir = Path.Combine(Path.GetTempPath(), "oce-" + Guid.NewGuid().ToString("N"));
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 }, null!, cts.Token));
            }
            finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
        }

        [Fact]
        public async Task UrlPath_NonCallerTimeout_RetriesThenFails_DoesNotEscapeAsCancellation()
        {
            var handler = new AlwaysTimeoutHandler();
            using var http = new HttpClient(handler);
            var orch = MakeUrl(http);
            var dir = Path.Combine(Path.GetTempPath(), "oce-" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 }, null!, CancellationToken.None);
                Assert.False(result.Success, "a request timeout is a track failure after retries, not a whole-album cancellation");
                Assert.True(handler.Calls > 1, $"the URL path should retry the non-caller timeout before failing (calls={handler.Calls})");
            }
            finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
        }
    }
}
