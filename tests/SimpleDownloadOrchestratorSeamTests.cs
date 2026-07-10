using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Download;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Covers the two protected extension seams on <see cref="SimpleDownloadOrchestrator"/> that let a
    /// plugin reuse the shared album loop while customizing track file naming and post-download payload
    /// validation (instead of overriding the entire <c>DownloadAlbumAsync</c> loop, as qobuzarr had to
    /// before these seams existed).
    /// </summary>
    public class SimpleDownloadOrchestratorSeamTests
    {
        private sealed class SeamOrchestrator : SimpleDownloadOrchestrator
        {
            public ConcurrentQueue<(string OutputDirectory, string? TrackId)> NamingCalls { get; } = new();
            public ConcurrentQueue<(string FilePath, string? TrackId)> ValidationCalls { get; } = new();

            public Func<string, StreamingTrack?, string>? NameBuilder { get; set; }
            public Action<string, StreamingTrack?>? Validator { get; set; }

            public SeamOrchestrator(
                HttpClient httpClient,
                Func<string, Task<StreamingAlbum>> getAlbumAsync,
                Func<string, Task<StreamingTrack>> getTrackAsync,
                Func<string, Task<IReadOnlyList<string>>> getAlbumTrackIdsAsync,
                Func<string, StreamingQuality?, Task<(string Url, string Extension)>> getStreamAsync,
                int maxConcurrentTracks = 1,
                IAudioStreamProvider? streamProvider = null,
                IAudioPostProcessor? postProcessor = null,
                IDownloadTelemetrySink? telemetrySink = null)
                : base(
                    "SeamTest",
                    httpClient,
                    getAlbumAsync,
                    getTrackAsync,
                    getAlbumTrackIdsAsync,
                    getStreamAsync,
                    maxConcurrentTracks,
                    streamProvider,
                    metadataApplier: new NoopMetadataApplier(),
                    logger: null,
                    postProcessor: postProcessor,
                    telemetrySink: telemetrySink)
            {
            }

            protected override string BuildTrackOutputPath(string outputDirectory, StreamingTrack? track)
            {
                NamingCalls.Enqueue((outputDirectory, track?.Id));
                return NameBuilder != null
                    ? NameBuilder(outputDirectory, track)
                    : base.BuildTrackOutputPath(outputDirectory, track);
            }

            protected override void ValidateDownloadedPayload(string filePath, StreamingTrack? track)
            {
                ValidationCalls.Enqueue((filePath, track?.Id));
                if (Validator != null)
                {
                    Validator(filePath, track);
                }
                else
                {
                    base.ValidateDownloadedPayload(filePath, track);
                }
            }

            private sealed class NoopMetadataApplier : IAudioMetadataApplier
            {
                public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
                    => Task.CompletedTask;
            }
        }

        private static StreamingAlbum Album(string id, int trackCount) => new()
        {
            Id = id,
            Title = "A",
            Artist = new StreamingArtist { Name = "X" },
            TrackCount = trackCount
        };

        private static StreamingTrack Track(string id, int number) => new()
        {
            Id = id,
            Title = $"T{number}",
            TrackNumber = number,
            Artist = new StreamingArtist { Name = "X" },
            Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }
        };

        private static SeamOrchestrator CreateUrlOrchestrator(HttpClient http, IReadOnlyList<string> trackIds, int maxConcurrentTracks = 1, IAudioPostProcessor? postProcessor = null)
        {
            return new SeamOrchestrator(
                http,
                getAlbumAsync: id => Task.FromResult(Album(id, trackIds.Count)),
                getTrackAsync: id => Task.FromResult(Track(id, int.Parse(id.TrimStart('t')))),
                getAlbumTrackIdsAsync: _ => Task.FromResult(trackIds),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")),
                maxConcurrentTracks: maxConcurrentTracks,
                postProcessor: postProcessor);
        }

        [Fact]
        public async Task DownloadAlbumAsync_SequentialLoop_UsesBuildTrackOutputPathSeam()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = CreateUrlOrchestrator(http, new List<string> { "t1", "t2" });
            orch.NameBuilder = (dir, track) => Path.Combine(dir, $"custom_{track?.TrackNumber:D2}.tmp");

            var dir = Path.Combine(Path.GetTempPath(), $"orch_seam_naming_seq_{Guid.NewGuid():N}");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"Album download failed: {result.ErrorMessage}");
                // The base engine swaps the extension to the resolved stream format ("bin").
                var expected = new[] { Path.Combine(dir, "custom_01.bin"), Path.Combine(dir, "custom_02.bin") };
                Assert.Equal(expected.OrderBy(p => p), result.FilePaths.OrderBy(p => p));
                Assert.All(expected, p => Assert.True(File.Exists(p), $"missing {p}"));
                Assert.Equal(2, orch.NamingCalls.Count);
                Assert.All(orch.NamingCalls, c => Assert.Equal(dir, c.OutputDirectory));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_ParallelLoop_UsesBuildTrackOutputPathSeam()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = CreateUrlOrchestrator(http, new List<string> { "t1", "t2" }, maxConcurrentTracks: 2);
            orch.NameBuilder = (dir, track) => Path.Combine(dir, $"custom_{track?.TrackNumber:D2}.tmp");

            var dir = Path.Combine(Path.GetTempPath(), $"orch_seam_naming_par_{Guid.NewGuid():N}");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"Album download failed: {result.ErrorMessage}");
                var expected = new[] { Path.Combine(dir, "custom_01.bin"), Path.Combine(dir, "custom_02.bin") };
                Assert.Equal(expected.OrderBy(p => p), result.FilePaths.OrderBy(p => p));
                Assert.Equal(2, orch.NamingCalls.Count);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_DefaultSeams_PreserveExistingNamingBehavior()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = CreateUrlOrchestrator(http, new List<string> { "t1" });

            var dir = Path.Combine(Path.GetTempPath(), $"orch_seam_naming_default_{Guid.NewGuid():N}");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"Album download failed: {result.ErrorMessage}");
                var expected = Path.ChangeExtension(
                    Path.Combine(dir, FileSystemUtilities.CreateTrackFileName("T1", 1)), "bin");
                var file = Assert.Single(result.FilePaths);
                Assert.Equal(expected, file);
                Assert.True(File.Exists(expected));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_PayloadValidationThrows_FailsTrackDeletesFileAndFailsAlbum()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = CreateUrlOrchestrator(http, new List<string> { "t1" });
            orch.Validator = (path, track) => throw new InvalidOperationException("payload validation failed: not audio");

            var dir = Path.Combine(Path.GetTempPath(), $"orch_seam_validate_fail_{Guid.NewGuid():N}");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 });

                Assert.False(result.Success);
                var tr = Assert.Single(result.TrackResults);
                Assert.False(tr.Success);
                Assert.Contains("payload validation failed", tr.ErrorMessage);
                Assert.Empty(result.FilePaths);
                // The rejected payload must not linger on disk.
                var call = Assert.Single(orch.ValidationCalls);
                Assert.False(File.Exists(call.FilePath), $"rejected payload still on disk: {call.FilePath}");
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_PayloadValidationThrows_FailsTrackAndDeletesFile()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = CreateUrlOrchestrator(http, new List<string> { "t1" });
            orch.Validator = (path, track) => throw new InvalidOperationException("payload validation failed: not audio");

            var temp = Path.Combine(Path.GetTempPath(), $"orch_seam_validate_track_{Guid.NewGuid():N}.tmp");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.False(result.Success);
                Assert.Contains("payload validation failed", result.ErrorMessage);
                var call = Assert.Single(orch.ValidationCalls);
                Assert.False(File.Exists(call.FilePath), $"rejected payload still on disk: {call.FilePath}");
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "bin"));
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_WithStreamProvider_PayloadValidationThrows_FailsTrack()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 1, supportRange: false));
            var provider = new StaticStreamProvider(payload: new byte[] { 1, 2, 3, 4 }, extension: "m4a");
            var orch = new SeamOrchestrator(
                http,
                getAlbumAsync: id => Task.FromResult(Album(id, 1)),
                getTrackAsync: id => Task.FromResult(Track(id, 1)),
                getAlbumTrackIdsAsync: _ => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "bin")),
                streamProvider: provider);
            orch.Validator = (path, track) => throw new InvalidOperationException("payload validation failed: not audio");

            var temp = Path.Combine(Path.GetTempPath(), $"orch_seam_validate_provider_{Guid.NewGuid():N}.tmp");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.False(result.Success);
                Assert.Contains("payload validation failed", result.ErrorMessage);
                var call = Assert.Single(orch.ValidationCalls);
                Assert.False(File.Exists(call.FilePath), $"rejected payload still on disk: {call.FilePath}");
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "m4a"));
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_PayloadValidationPasses_ReceivesFinalPathAfterPostProcessing()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = CreateUrlOrchestrator(http, new List<string> { "t1" }, postProcessor: new ExtensionSwapPostProcessor("flac"));

            var temp = Path.Combine(Path.GetTempPath(), $"orch_seam_validate_final_{Guid.NewGuid():N}.tmp");
            var expectedFlac = Path.ChangeExtension(temp, "flac");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                var call = Assert.Single(orch.ValidationCalls);
                // Validation must see the FINAL file (post-processed path), matching what ships to the library.
                Assert.Equal(expectedFlac, call.FilePath);
                Assert.Equal("t1", call.TrackId);
                Assert.True(File.Exists(expectedFlac));
            }
            finally
            {
                TryDelete(temp);
                TryDelete(expectedFlac);
                TryDelete(Path.ChangeExtension(temp, "bin"));
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_FailedDownload_DoesNotInvokePayloadValidation()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = new SeamOrchestrator(
                http,
                getAlbumAsync: id => Task.FromResult(Album(id, 1)),
                getTrackAsync: id => Task.FromResult(Track(id, 1)),
                getAlbumTrackIdsAsync: _ => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult((string.Empty, string.Empty)));

            var dir = Path.Combine(Path.GetTempPath(), $"orch_seam_validate_skipfail_{Guid.NewGuid():N}");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 });

                Assert.False(result.Success);
                Assert.Empty(orch.ValidationCalls);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_ParallelLoop_ValidationFailureOnOneTrack_FailsAlbumKeepsGoodTrack()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var sink = new RecordingTelemetrySink();
            var orch = new SeamOrchestrator(
                http,
                getAlbumAsync: id => Task.FromResult(Album(id, 2)),
                getTrackAsync: id => Task.FromResult(Track(id, int.Parse(id.TrimStart('t')))),
                getAlbumTrackIdsAsync: _ => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1", "t2" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")),
                maxConcurrentTracks: 2,
                telemetrySink: sink);
            orch.Validator = (path, track) =>
            {
                if (track?.Id == "t2") throw new InvalidOperationException("payload validation failed: not audio");
            };

            var dir = Path.Combine(Path.GetTempPath(), $"orch_seam_validate_par_{Guid.NewGuid():N}");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 });

                Assert.False(result.Success);
                Assert.Equal(2, result.TrackResults.Count);
                Assert.Single(result.TrackResults, tr => tr.Success);
                var failed = Assert.Single(result.TrackResults, tr => !tr.Success);
                Assert.Equal("t2", failed.TrackId);
                var goodFile = Assert.Single(result.FilePaths);
                Assert.True(File.Exists(goodFile));
                // Telemetry must reflect the FINAL (post-validation) result per track.
                Assert.False(sink.ByTrackId["t2"].Success);
                Assert.True(sink.ByTrackId["t1"].Success);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_ValidatorThrowsNonCallerOce_ConvertsToTrackFailureAndDeletesFile()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = CreateUrlOrchestrator(http, new List<string> { "t1" });
            // A validator-internal timeout (OCE while the caller token is NOT cancelled) must be a track
            // failure — never escape as a whole-album cancel.
            orch.Validator = (path, track) => throw new OperationCanceledException("validator-internal timeout");

            var temp = Path.Combine(Path.GetTempPath(), $"orch_seam_validate_oce_int_{Guid.NewGuid():N}.tmp");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.False(result.Success);
                var call = Assert.Single(orch.ValidationCalls);
                Assert.False(File.Exists(call.FilePath));
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "bin"));
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_ValidatorThrowsCallerCancellation_PropagatesAsCancel()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            using var cts = new CancellationTokenSource();
            var orch = CreateUrlOrchestrator(http, new List<string> { "t1" });
            orch.Validator = (path, track) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            };

            var temp = Path.Combine(Path.GetTempPath(), $"orch_seam_validate_oce_caller_{Guid.NewGuid():N}.tmp");
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, cts.Token));
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "bin"));
            }
        }

        private sealed class RecordingTelemetrySink : IDownloadTelemetrySink
        {
            private readonly ConcurrentDictionary<string, DownloadTelemetry> _byTrackId = new();

            public IReadOnlyDictionary<string, DownloadTelemetry> ByTrackId => _byTrackId;

            public void OnTrackCompleted(DownloadTelemetry telemetry)
            {
                if (telemetry.TrackId != null)
                {
                    _byTrackId[telemetry.TrackId] = telemetry;
                }
            }
        }

        private sealed class StaticStreamProvider : IAudioStreamProvider
        {
            private readonly byte[] _payload;
            private readonly string _extension;

            public StaticStreamProvider(byte[] payload, string extension)
            {
                _payload = payload;
                _extension = extension;
            }

            public Task<AudioStreamResult> GetStreamAsync(string trackId, StreamingQuality? quality = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AudioStreamResult
                {
                    Stream = new MemoryStream(_payload, writable: false),
                    TotalBytes = _payload.Length,
                    SuggestedExtension = _extension
                });
            }
        }

        private sealed class ExtensionSwapPostProcessor : IAudioPostProcessor
        {
            private readonly string _extension;

            public ExtensionSwapPostProcessor(string extension)
            {
                _extension = extension.TrimStart('.');
            }

            public Task<string> PostProcessAsync(string filePath, StreamingTrack track, StreamingQuality? quality, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextPath = Path.ChangeExtension(filePath, _extension);
                if (string.Equals(nextPath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(filePath);
                }

                File.Move(filePath, nextPath, overwrite: true);
                return Task.FromResult(nextPath);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
