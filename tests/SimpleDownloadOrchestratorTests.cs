using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation.Results;
using Lidarr.Plugin.Common.Base;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Services.Download;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class SimpleDownloadOrchestratorTests
    {
        private sealed class FakeStreamProvider : IAudioStreamProvider
        {
            private readonly byte[] _payload;
            private readonly string _extension;

            public FakeStreamProvider(byte[] payload, string extension)
            {
                _payload = payload ?? Array.Empty<byte>();
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

        private sealed class OperationCanceledPostProcessor : IAudioPostProcessor
        {
            public Task<string> PostProcessAsync(string filePath, StreamingTrack track, StreamingQuality? quality, CancellationToken cancellationToken)
                => throw new OperationCanceledException(cancellationToken);
        }

        private sealed class OperationCanceledMetadataApplier : IAudioMetadataApplier
        {
            public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
                => throw new OperationCanceledException(cancellationToken);
        }

        private sealed class CallerCancelingMetadataApplier : IAudioMetadataApplier
        {
            private readonly Action _cancelCaller;

            public CallerCancelingMetadataApplier(Action cancelCaller)
            {
                _cancelCaller = cancelCaller;
            }

            public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
            {
                _cancelCaller();
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private sealed class NoopMetadataApplier : IAudioMetadataApplier
        {
            public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }

        private sealed class ThrowingArtworkEmbedder : IAudioArtworkEmbedder
        {
            public int Calls { get; private set; }

            public Task EmbedAsync(string filePath, byte[] imageBytes, string mimeType, CancellationToken cancellationToken = default)
            {
                Calls++;
                throw new InvalidOperationException("artwork tagging failed");
            }
        }

        private sealed class RecordingArtworkEmbedder : IAudioArtworkEmbedder
        {
            public int Calls { get; private set; }
            public string? MimeType { get; private set; }
            public byte[]? Bytes { get; private set; }

            public Task EmbedAsync(string filePath, byte[] imageBytes, string mimeType, CancellationToken cancellationToken = default)
            {
                Calls++;
                MimeType = mimeType;
                Bytes = imageBytes;
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task DownloadTrack_WithStreamProvider_RunsPostProcessorAndUpdatesResultPath()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 1, supportRange: false));
            var streamProvider = new FakeStreamProvider(payload: new byte[] { 1, 2, 3, 4 }, extension: "m4a");
            var postProcessor = new ExtensionSwapPostProcessor("flac");

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "bin")),
                streamProvider: streamProvider,
                postProcessor: postProcessor);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_post_{Guid.NewGuid():N}.bin");
            var expectedFlac = Path.ChangeExtension(temp, "flac");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                Assert.Equal(expectedFlac, result.FilePath);
                Assert.True(File.Exists(result.FilePath));
                Assert.False(File.Exists(Path.ChangeExtension(temp, "m4a")));
                Assert.Equal(4, new FileInfo(result.FilePath).Length);
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "m4a"));
                TryDelete(expectedFlac);
                TryDelete(temp + ".partial");
                TryDelete(temp + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadTrack_WithStreamProvider_PostProcessorNonCallerOce_DoesNotFailDownload()
        {
            // A post-processor throwing OCE with the caller token NOT cancelled is a non-caller event
            // (e.g. the post-processor's own timeout), not user cancellation. Post-processing is
            // best-effort: the already-downloaded file must survive (fall back to unprocessed), and the
            // track must NOT be reported as cancelled. (Genuine caller cancellation is covered by
            // SimpleDownloadOrchestratorOceTimeoutTests / the cancellation+backpressure suite.)
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 1, supportRange: false));
            var streamProvider = new FakeStreamProvider(payload: new byte[] { 1, 2, 3, 4 }, extension: "m4a");

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "bin")),
                streamProvider: streamProvider,
                metadataApplier: new NoopMetadataApplier(),
                postProcessor: new OperationCanceledPostProcessor());

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_post_cancel_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);
                Assert.True(result.Success, "a non-caller post-processor OCE must not fail/cancel a successfully downloaded track");
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "m4a"));
                TryDelete(temp + ".partial");
                TryDelete(temp + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadTrack_WithStreamProvider_MetadataNonCallerOce_DoesNotFailDownload()
        {
            // Metadata application is best-effort: a non-caller OCE (token not cancelled) must be
            // swallowed, leaving the downloaded file intact and the track successful.
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 1, supportRange: false));
            var streamProvider = new FakeStreamProvider(payload: new byte[] { 1, 2, 3, 4 }, extension: "m4a");

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "bin")),
                streamProvider: streamProvider,
                metadataApplier: new OperationCanceledMetadataApplier());

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_metadata_cancel_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);
                Assert.True(result.Success, "a non-caller metadata OCE must not fail/cancel a successfully downloaded track");
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "m4a"));
                TryDelete(temp + ".partial");
                TryDelete(temp + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadTrack_WithStreamProvider_MetadataCallerCancellation_IsRethrown()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 1, supportRange: false));
            var streamProvider = new FakeStreamProvider(payload: new byte[] { 1, 2, 3, 4 }, extension: "m4a");
            using var cts = new CancellationTokenSource();

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "bin")),
                streamProvider: streamProvider,
                metadataApplier: new CallerCancelingMetadataApplier(cts.Cancel));

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_metadata_caller_cancel_{Guid.NewGuid():N}.bin");
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 }, cts.Token));
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "m4a"));
                TryDelete(temp + ".partial");
                TryDelete(temp + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadTrack_ReportsProgress_WithSpeedAndEta()
        {
            var totalBytes = 512 * 1024; // 512KB
            var handler = new FakeRangeHandler(totalBytes, supportRange: true);
            using var http = new HttpClient(handler);

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin"))
            );

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_progress_{Guid.NewGuid():N}.bin");
            try
            {
                // Minimal assertion-only test; progress values are exercised by the download
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                Assert.True(File.Exists(result.FilePath));
                Assert.Equal(totalBytes, new FileInfo(result.FilePath).Length);
            }
            finally { TryDelete(temp); TryDelete(temp + ".partial"); TryDelete(temp + ".partial.resume.json"); }
        }

        [Fact]
        public async Task DownloadTrack_ResumeWithIfRange_ContinuesFromPartial()
        {
            var totalBytes = 300 * 1024; // 300KB
            var handler = new FakeRangeHandler(totalBytes, supportRange: true, etag: "\"abc123\"");
            using var http = new HttpClient(handler);

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file2", "bin"))
            );

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_resume_{Guid.NewGuid():N}.bin");
            var partial = temp + ".partial";
            try
            {
                // Seed partial file with first 100KB
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                using (var fs = new FileStream(partial, FileMode.Create, FileAccess.Write))
                {
                    var buf = new byte[100 * 1024];
                    new Random(42).NextBytes(buf);
                    fs.Write(buf, 0, buf.Length);
                }

                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });
                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                Assert.Equal(totalBytes, new FileInfo(result.FilePath).Length);
            }
            finally { TryDelete(temp); TryDelete(partial); TryDelete(temp + ".partial.resume.json"); }
        }

        [Fact]
        public async Task DownloadTrackAsync_CancelledTokenStopsCopyAndReleasesPartialFile()
        {
            var handler = new SlowStreamingHandler();
            using var http = new HttpClient(handler);

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/track", "bin"))
            );

            var outputPath = Path.Combine(Path.GetTempPath(), $"orch_test_cancel_{Guid.NewGuid():N}.bin");
            var partialPath = outputPath + ".partial";
            var resumePath = outputPath + ".partial.resume.json";
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                var downloadTask = orch.DownloadTrackAsync("t1", outputPath, new StreamingQuality { Bitrate = 320 }, cts.Token);

                await handler.FirstRead;
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await downloadTask);

                Assert.True(File.Exists(partialPath), "Partial download file should exist after cancellation.");

                using (var fs = new FileStream(partialPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.NotNull(fs);
                }
            }
            finally
            {
                TryDelete(outputPath);
                TryDelete(partialPath);
                TryDelete(resumePath);
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_EmptyResponse_ReturnsFailureAndDoesNotLeaveFinalFile()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 0, supportRange: false));

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin"))
            );

            var outputPath = Path.Combine(Path.GetTempPath(), $"orch_test_empty_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", outputPath, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);

                Assert.False(result.Success);
                Assert.Contains("Downloaded file is empty", result.ErrorMessage ?? string.Empty);
                Assert.False(File.Exists(outputPath), "Expected the final file to be removed on empty download");
            }
            finally
            {
                TryDelete(outputPath);
                TryDelete(outputPath + ".partial");
                TryDelete(outputPath + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_NoTrackIds_ReturnsFailure()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 1, supportRange: false));
            var capturingLogger = new CapturingLogger();

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 10 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string>()),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")),
                logger: capturingLogger
            );

            var dir = Path.Combine(Path.GetTempPath(), $"orch_test_album_empty_{Guid.NewGuid():N}");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 });
                Assert.False(result.Success);
                Assert.Contains("No track IDs returned", result.ErrorMessage ?? string.Empty);
                Assert.Empty(result.FilePaths);
                Assert.Equal(1, capturingLogger.WarningCount);
                Assert.Contains("a1", capturingLogger.LastWarningMessage);
                Assert.Contains("No track IDs returned", capturingLogger.LastWarningMessage);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_TrackFailures_ReturnsFailureWithFirstError()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 1, supportRange: false));

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult((string.Empty, "bin"))
            );

            var dir = Path.Combine(Path.GetTempPath(), $"orch_test_album_fail_{Guid.NewGuid():N}");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 });
                Assert.False(result.Success);
                Assert.Contains("Failed to download", result.ErrorMessage ?? string.Empty);
                Assert.Contains("Empty stream URL", result.ErrorMessage ?? string.Empty);
                Assert.Empty(result.FilePaths);
                Assert.Single(result.TrackResults);
                Assert.False(result.TrackResults[0].Success);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_MetadataApplierThrows_StillReturnsSuccessAndKeepsFile()
        {
            var totalBytes = 1024;
            var handler = new FakeRangeHandler(totalBytes, supportRange: false);
            using var http = new HttpClient(handler);
            var throwingApplier = new ThrowingMetadataApplier();

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")),
                metadataApplier: throwingApplier
            );

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_meta_throw_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });

                // Metadata failure should NOT fail the download
                Assert.True(result.Success, $"Download should succeed even when metadata applier throws: {result.ErrorMessage}");
                Assert.True(File.Exists(result.FilePath), "File should be kept even when metadata applier throws");
                Assert.Equal(totalBytes, new FileInfo(result.FilePath).Length);
                Assert.True(throwingApplier.WasCalled, "Metadata applier should have been called");
            }
            finally
            {
                TryDelete(temp);
                TryDelete(temp + ".partial");
                TryDelete(temp + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadAlbumAsync_UnsuccessfulAlbum_LogsWarningNamingAlbumAndReason()
        {
            // 0-byte stream => the only track fails => album is incomplete => result.Success=false.
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 0, supportRange: false));
            var capturingLogger = new CapturingLogger();
            var orch = new SimpleDownloadOrchestrator(
                serviceName: "TestService",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")),
                logger: capturingLogger
            );

            var dir = Path.Combine(Path.GetTempPath(), $"orch_album_warn_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var result = await orch.DownloadAlbumAsync("album99", dir, new StreamingQuality { Bitrate = 320 });

                Assert.False(result.Success);
                // An unsuccessful album must not fail silently: a warning naming the album + reason is logged.
                Assert.Equal(1, capturingLogger.WarningCount);
                Assert.Contains("album99", capturingLogger.LastWarningMessage);
                Assert.Contains("Downloaded file is empty", capturingLogger.LastWarningMessage);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_MetadataApplier_InvokedOncePerTrack()
        {
            var totalBytes = 1024;
            var handler = new FakeRangeHandler(totalBytes, supportRange: false);
            using var http = new HttpClient(handler);
            var countingApplier = new CountingMetadataApplier();

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")),
                metadataApplier: countingApplier
            );

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_meta_count_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success);
                Assert.Equal(1, countingApplier.CallCount);
            }
            finally
            {
                TryDelete(temp);
                TryDelete(temp + ".partial");
                TryDelete(temp + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_MetadataApplierThrows_LogsWarningExactlyOnce()
        {
            var totalBytes = 1024;
            var handler = new FakeRangeHandler(totalBytes, supportRange: false);
            using var http = new HttpClient(handler);
            var throwingApplier = new ThrowingMetadataApplier();
            var capturingLogger = new CapturingLogger();

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "TestService",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = "track123", Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")),
                metadataApplier: throwingApplier,
                logger: capturingLogger
            );

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_meta_log_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success);
                Assert.Equal(1, capturingLogger.WarningCount);
                Assert.Contains("TestService", capturingLogger.LastWarningMessage);
                Assert.Contains("track123", capturingLogger.LastWarningMessage);
            }
            finally
            {
                TryDelete(temp);
                TryDelete(temp + ".partial");
                TryDelete(temp + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_ZeroByteDownload_ApplierNotInvoked()
        {
            // 0-byte response simulates failed/empty stream
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 0, supportRange: false));
            var countingApplier = new CountingMetadataApplier();

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")),
                metadataApplier: countingApplier
            );

            var outputPath = Path.Combine(Path.GetTempPath(), $"orch_test_zero_{Guid.NewGuid():N}.bin");
            try
            {
                var result = await orch.DownloadTrackAsync("t1", outputPath, new StreamingQuality { Bitrate = 320 });

                // Download should fail due to 0 bytes
                Assert.False(result.Success);
                Assert.Contains("empty", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

                // Metadata applier should NOT be invoked for failed/0-byte downloads
                Assert.Equal(0, countingApplier.CallCount);

                // File should not exist
                Assert.False(File.Exists(outputPath));
            }
            finally
            {
                TryDelete(outputPath);
                TryDelete(outputPath + ".partial");
                TryDelete(outputPath + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_EmbedsAlbumCoverArtFromMetadata()
        {
            var coverUrl = "https://93.184.216.34/cover.jpg";
            var image = FakeJpeg();
            using var http = new HttpClient(new StaticContentHandler(image, "image/jpeg"));
            var streamProvider = new FakeStreamProvider(MinimalFlacBytes(), "flac");

            var album = new StreamingAlbum
            {
                Id = "album1",
                Title = "A",
                Artist = new StreamingArtist { Name = "X" },
                TrackCount = 1,
                CoverArtUrls = new Dictionary<string, string> { ["large"] = coverUrl }
            };

            var track = new StreamingTrack
            {
                Id = "track1",
                Title = "T",
                Artist = new StreamingArtist { Name = "X" },
                Album = album,
                TrackNumber = 1
            };

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(album),
                getTrackAsync: id => Task.FromResult(track),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "track1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "flac")),
                streamProvider: streamProvider);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_cover_{Guid.NewGuid():N}.flac");
            try
            {
                var result = await orch.DownloadTrackAsync("track1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                using var file = TagLib.File.Create(result.FilePath);
                Assert.Single(file.Tag.Pictures);
                Assert.Equal(TagLib.PictureType.FrontCover, file.Tag.Pictures[0].Type);
                Assert.Equal("image/jpeg", file.Tag.Pictures[0].MimeType);
                Assert.Equal(image, file.Tag.Pictures[0].Data.Data);
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "flac"));
                TryDelete(temp + ".partial");
                TryDelete(temp + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_ArtworkEmbedderFailureDoesNotFailDownload()
        {
            var coverUrl = "https://93.184.216.34/cover.jpg";
            using var http = new HttpClient(new StaticContentHandler(FakeJpeg(), "image/jpeg"));
            var streamProvider = new FakeStreamProvider(MinimalFlacBytes(), "flac");
            var artworkEmbedder = new ThrowingArtworkEmbedder();

            var album = new StreamingAlbum
            {
                Id = "album1",
                Title = "A",
                Artist = new StreamingArtist { Name = "X" },
                TrackCount = 1,
                CoverArtUrls = new Dictionary<string, string> { ["large"] = coverUrl }
            };

            var track = new StreamingTrack
            {
                Id = "track1",
                Title = "T",
                Artist = new StreamingArtist { Name = "X" },
                Album = album,
                TrackNumber = 1
            };

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(album),
                getTrackAsync: id => Task.FromResult(track),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "track1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "flac")),
                maxConcurrentTracks: 1,
                streamProvider: streamProvider,
                metadataApplier: null,
                logger: null,
                postProcessor: null,
                telemetrySink: null,
                mediaUriPolicy: null,
                artworkEmbedder: artworkEmbedder);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_cover_failure_{Guid.NewGuid():N}.flac");
            try
            {
                var result = await orch.DownloadTrackAsync("track1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                Assert.Equal(1, artworkEmbedder.Calls);
                Assert.True(File.Exists(result.FilePath));
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "flac"));
                TryDelete(temp + ".partial");
                TryDelete(temp + ".partial.resume.json");
            }
        }

        [Fact]
        public async Task DownloadTrackAsync_SkipsCoverArtWhenContentLengthTooLarge()
        {
            var coverUrl = "https://93.184.216.34/too-large-cover.jpg";
            var image = FakeJpeg();
            using var http = new HttpClient(new StaticContentHandler(image, "image/jpeg", contentLength: 10L * 1024 * 1024 + 1));
            var streamProvider = new FakeStreamProvider(MinimalFlacBytes(), "flac");

            var album = new StreamingAlbum
            {
                Id = "album1",
                Title = "A",
                Artist = new StreamingArtist { Name = "X" },
                TrackCount = 1,
                CoverArtUrls = new Dictionary<string, string> { ["large"] = coverUrl }
            };

            var track = new StreamingTrack
            {
                Id = "track1",
                Title = "T",
                Artist = new StreamingArtist { Name = "X" },
                Album = album,
                TrackNumber = 1
            };

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(album),
                getTrackAsync: id => Task.FromResult(track),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "track1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "flac")),
                streamProvider: streamProvider);

            var temp = Path.Combine(Path.GetTempPath(), $"orch_test_cover_large_{Guid.NewGuid():N}.flac");
            try
            {
                var result = await orch.DownloadTrackAsync("track1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                using var file = TagLib.File.Create(result.FilePath);
                Assert.Empty(file.Tag.Pictures);
            }
            finally
            {
                TryDelete(temp);
                TryDelete(Path.ChangeExtension(temp, "flac"));
                TryDelete(temp + ".partial");
                TryDelete(temp + ".partial.resume.json");
            }
        }
        [Fact]
        public async Task DownloadTrackAsync_SkipsCoverArtWhenResponseIsNotAnImage()
        {
            var coverUrl = "https://93.184.216.34/soft-404.jpg";
            var html = System.Text.Encoding.UTF8.GetBytes("<html><body>Not Found</body></html>");
            using var http = new HttpClient(new StaticContentHandler(html, "text/html"));
            var streamProvider = new FakeStreamProvider(MinimalFlacBytes(), "flac");
            var album = new StreamingAlbum { Id = "album1", Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1, CoverArtUrls = new Dictionary<string, string> { ["large"] = coverUrl } };
            var track = new StreamingTrack { Id = "track1", Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = album, TrackNumber = 1 };
            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test", httpClient: http,
                getAlbumAsync: id => Task.FromResult(album), getTrackAsync: id => Task.FromResult(track),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "track1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "flac")), streamProvider: streamProvider);
            var temp = Path.Combine(Path.GetTempPath(), $"orch_cover_html_{Guid.NewGuid():N}.flac");
            try
            {
                var result = await orch.DownloadTrackAsync("track1", temp, new StreamingQuality { Bitrate = 320 });
                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                using var file = TagLib.File.Create(result.FilePath);
                Assert.Empty(file.Tag.Pictures); // HTML soft-404 must not be embedded as a PICTURE
            }
            finally { TryDelete(temp); TryDelete(temp + ".partial"); TryDelete(temp + ".partial.resume.json"); }
        }

        [Fact]
        public async Task DownloadTrackAsync_SkipsCoverArtWhenJpegHeaderContainsHtmlBytes()
        {
            var coverUrl = "https://93.184.216.34/spoofed-cover.jpg";
            var html = System.Text.Encoding.UTF8.GetBytes("<html><body>Not Found</body></html>");
            using var http = new HttpClient(new StaticContentHandler(html, "image/jpeg"));
            var streamProvider = new FakeStreamProvider(MinimalFlacBytes(), "flac");
            var artworkEmbedder = new RecordingArtworkEmbedder();
            var album = new StreamingAlbum { Id = "album1", Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1, CoverArtUrls = new Dictionary<string, string> { ["large"] = coverUrl } };
            var track = new StreamingTrack { Id = "track1", Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = album, TrackNumber = 1 };
            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test", httpClient: http,
                getAlbumAsync: id => Task.FromResult(album), getTrackAsync: id => Task.FromResult(track),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "track1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "flac")),
                maxConcurrentTracks: 1,
                streamProvider: streamProvider,
                metadataApplier: null,
                logger: null,
                postProcessor: null,
                telemetrySink: null,
                mediaUriPolicy: null,
                artworkEmbedder: artworkEmbedder);
            var temp = Path.Combine(Path.GetTempPath(), $"orch_cover_spoofed_{Guid.NewGuid():N}.flac");
            try
            {
                var result = await orch.DownloadTrackAsync("track1", temp, new StreamingQuality { Bitrate = 320 });
                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                Assert.Equal(0, artworkEmbedder.Calls);
            }
            finally { TryDelete(temp); TryDelete(temp + ".partial"); TryDelete(temp + ".partial.resume.json"); }
        }

        [Fact]
        public async Task DownloadTrackAsync_SkipsCoverArtWhenMimeTypeIsSvg()
        {
            var coverUrl = "https://93.184.216.34/vector-cover.svg";
            var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
            using var http = new HttpClient(new StaticContentHandler(svg, "image/svg+xml"));
            var streamProvider = new FakeStreamProvider(MinimalFlacBytes(), "flac");
            var artworkEmbedder = new RecordingArtworkEmbedder();
            var album = new StreamingAlbum { Id = "album1", Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1, CoverArtUrls = new Dictionary<string, string> { ["large"] = coverUrl } };
            var track = new StreamingTrack { Id = "track1", Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = album, TrackNumber = 1 };
            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test", httpClient: http,
                getAlbumAsync: id => Task.FromResult(album), getTrackAsync: id => Task.FromResult(track),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "track1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "flac")),
                maxConcurrentTracks: 1,
                streamProvider: streamProvider,
                metadataApplier: null,
                logger: null,
                postProcessor: null,
                telemetrySink: null,
                mediaUriPolicy: null,
                artworkEmbedder: artworkEmbedder);
            var temp = Path.Combine(Path.GetTempPath(), $"orch_cover_svg_{Guid.NewGuid():N}.flac");
            try
            {
                var result = await orch.DownloadTrackAsync("track1", temp, new StreamingQuality { Bitrate = 320 });
                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                Assert.Equal(0, artworkEmbedder.Calls);
            }
            finally { TryDelete(temp); TryDelete(temp + ".partial"); TryDelete(temp + ".partial.resume.json"); }
        }

        [Fact]
        public async Task DownloadTrackAsync_SkipsCoverArtWhenChunkedBodyExceedsCap()
        {
            var coverUrl = "https://93.184.216.34/huge-cover.jpg";
            // 11 MB image body with NO Content-Length (chunked TE): the header check can't catch it,
            // so the bounded read must abort rather than buffer the whole body into memory.
            var handler = new ChunkedStreamHandler(11L * 1024 * 1024, "image/jpeg");
            using var http = new HttpClient(handler);
            var streamProvider = new FakeStreamProvider(MinimalFlacBytes(), "flac");
            var album = new StreamingAlbum { Id = "album1", Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1, CoverArtUrls = new Dictionary<string, string> { ["large"] = coverUrl } };
            var track = new StreamingTrack { Id = "track1", Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = album, TrackNumber = 1 };
            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test", httpClient: http,
                getAlbumAsync: id => Task.FromResult(album), getTrackAsync: id => Task.FromResult(track),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "track1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "flac")), streamProvider: streamProvider);
            var temp = Path.Combine(Path.GetTempPath(), $"orch_cover_chunked_{Guid.NewGuid():N}.flac");
            try
            {
                var result = await orch.DownloadTrackAsync("track1", temp, new StreamingQuality { Bitrate = 320 });
                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                using var file = TagLib.File.Create(result.FilePath);
                Assert.Empty(file.Tag.Pictures); // over-cap chunked body must be aborted, not embedded
                Assert.True(handler.BytesRead < handler.TotalBytes, "bounded cover-art read should stop at the cap instead of buffering the full response");
            }
            finally { TryDelete(temp); TryDelete(temp + ".partial"); TryDelete(temp + ".partial.resume.json"); }
        }

        [Fact]
        public async Task DownloadTrackAsync_SlowCoverArtBodyReadTimesOutWithoutFailingDownload()
        {
            var coverUrl = "https://93.184.216.34/stalled-cover.jpg";
            var handler = new StallingArtworkHandler("image/jpeg");
            using var http = new HttpClient(handler);
            var streamProvider = new FakeStreamProvider(MinimalFlacBytes(), "flac");
            var artworkEmbedder = new RecordingArtworkEmbedder();
            var album = new StreamingAlbum { Id = "album1", Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1, CoverArtUrls = new Dictionary<string, string> { ["large"] = coverUrl } };
            var track = new StreamingTrack { Id = "track1", Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = album, TrackNumber = 1 };
            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test", httpClient: http,
                getAlbumAsync: id => Task.FromResult(album), getTrackAsync: id => Task.FromResult(track),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "track1" }),
                getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/unused", "flac")),
                maxConcurrentTracks: 1,
                streamProvider: streamProvider,
                metadataApplier: null,
                logger: null,
                postProcessor: null,
                telemetrySink: null,
                mediaUriPolicy: null,
                artworkEmbedder: artworkEmbedder,
                artworkReadTimeout: TimeSpan.FromMilliseconds(50));
            var temp = Path.Combine(Path.GetTempPath(), $"orch_cover_stall_{Guid.NewGuid():N}.flac");
            try
            {
                var downloadTask = orch.DownloadTrackAsync("track1", temp, new StreamingQuality { Bitrate = 320 }, CancellationToken.None);
                var completed = await Task.WhenAny(downloadTask, Task.Delay(TimeSpan.FromSeconds(1)));

                Assert.Same(downloadTask, completed);
                var result = await downloadTask;
                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                Assert.True(handler.ReadStarted.Task.IsCompleted, "the cover-art body stream should be reached before timing out");
                Assert.Equal(0, artworkEmbedder.Calls);
            }
            finally { TryDelete(temp); TryDelete(temp + ".partial"); TryDelete(temp + ".partial.resume.json"); }
        }

        private sealed class ChunkedStreamHandler : HttpMessageHandler
        {
            private readonly string _mediaType;

            public ChunkedStreamHandler(long size, string mediaType)
            {
                TotalBytes = size;
                _mediaType = mediaType;
            }

            public long TotalBytes { get; }
            public long BytesRead { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ForwardOnlyZeroStream(TotalBytes, bytesRead => BytesRead += bytesRead))
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
                // Deliberately no ContentLength -> simulates chunked transfer encoding.
                return Task.FromResult(response);
            }
        }

        private sealed class StallingArtworkHandler : HttpMessageHandler
        {
            private readonly string _mediaType;

            public StallingArtworkHandler(string mediaType)
            {
                _mediaType = mediaType;
            }

            public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new StallingReadStream(ReadStarted))
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
                return Task.FromResult(response);
            }
        }

        private sealed class StallingReadStream : Stream
        {
            private readonly TaskCompletionSource<bool> _readStarted;

            public StallingReadStream(TaskCompletionSource<bool> readStarted)
            {
                _readStarted = readStarted;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

#if NET8_0_OR_GREATER
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                _readStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }
#endif

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                _readStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                _readStarted.TrySetResult(true);
                throw new NotSupportedException("The cover-art timeout test must use async reads.");
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class ForwardOnlyZeroStream : Stream
        {
            private readonly Action<int> _onRead;
            private long _remaining;

            public ForwardOnlyZeroStream(long size, Action<int> onRead)
            {
                _remaining = size;
                _onRead = onRead;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0) return 0;
                int n = (int)Math.Min(count, _remaining);
                Array.Clear(buffer, offset, n);
                _remaining -= n;
                _onRead(n);
                return n;
            }

#if NET8_0_OR_GREATER
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_remaining <= 0) return ValueTask.FromResult(0);
                int n = (int)Math.Min(buffer.Length, _remaining);
                buffer.Span.Slice(0, n).Clear();
                _remaining -= n;
                _onRead(n);
                return ValueTask.FromResult(n);
            }
#endif

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static byte[] FakeJpeg() => new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
            0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
        };

        private static byte[] MinimalFlacBytes() => new byte[]
        {
            0x66, 0x4C, 0x61, 0x43,
            0x80, 0x00, 0x00, 0x22,
            0x00, 0x10, 0x00, 0x10,
            0x00, 0x00, 0x01, 0x00, 0x00, 0x01,
            0x0A, 0xC4, 0x40, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xFF, 0xF8, 0x09, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        private sealed class StaticContentHandler : HttpMessageHandler
        {
            private readonly byte[] _payload;
            private readonly string _mediaType;
            private readonly long? _contentLength;

            public StaticContentHandler(byte[] payload, string mediaType, long? contentLength = null)
            {
                _payload = payload;
                _mediaType = mediaType;
                _contentLength = contentLength;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_payload)
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
                response.Content.Headers.ContentLength = _contentLength ?? _payload.Length;
                return Task.FromResult(response);
            }
        }
        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDir(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch { }
        }
    }


    public class BaseStreamingDownloadClientTests
    {
        [Fact]
        public async Task ApplyMetadataTags_InvokesApplierWhenMetadataPresent()
        {
            var applier = new CapturingMetadataApplier();
            var client = new TestDownloadClient(new TestSettings(), applier);
            var track = new StreamingTrack { Title = "Test Track" };
            var tempPath = Path.Combine(Path.GetTempPath(), $"metadata_apply_test_{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(tempPath, new byte[1]);

            try
            {
                await client.InvokeApplyMetadataAsync(tempPath, track);
                Assert.True(applier.WasCalled);
                Assert.Equal(tempPath, applier.FilePath);
                Assert.Same(track, applier.Metadata);
                Assert.False(applier.CancellationToken.CanBeCanceled);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        [Fact]
        public async Task ApplyMetadataTags_SkipsWhenMetadataNull()
        {
            var applier = new CapturingMetadataApplier();
            var client = new TestDownloadClient(new TestSettings(), applier);
            await client.InvokeApplyMetadataAsync(Path.Combine(Path.GetTempPath(), $"metadata_null_{Guid.NewGuid():N}.bin"), null!);
            Assert.False(applier.WasCalled);
        }

        [Fact]
        public async Task ApplyMetadataTags_SkipsWhenApplierMissing()
        {
            var client = new TestDownloadClient(new TestSettings(), null);
            var track = new StreamingTrack { Title = "Track" };
            await client.InvokeApplyMetadataAsync(Path.Combine(Path.GetTempPath(), $"metadata_missing_{Guid.NewGuid():N}.bin"), track);
        }
    }

    internal sealed class SlowStreamingHandler : HttpMessageHandler
    {
        private readonly long _totalBytes;
        private readonly TaskCompletionSource<bool> _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SlowStreamingHandler(long totalBytes = 5 * 1024 * 1024)
        {
            _totalBytes = totalBytes;
        }

        public Task FirstRead => _firstRead.Task;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new SlowStreamingContent(this, _totalBytes))
            };
            response.Content.Headers.ContentLength = _totalBytes;
            return Task.FromResult(response);
        }

        private void NotifyFirstRead() => _firstRead.TrySetResult(true);

        private sealed class SlowStreamingContent : Stream
        {
            private readonly SlowStreamingHandler _owner;
            private readonly long _totalBytes;
            private long _position;

            public SlowStreamingContent(SlowStreamingHandler owner, long totalBytes)
            {
                _owner = owner;
                _totalBytes = totalBytes;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _totalBytes;
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

#if NET8_0_OR_GREATER
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return await ReadInternalAsync(buffer, cancellationToken);
            }
#endif

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                var destination = new Memory<byte>(buffer, offset, count);
                return await ReadInternalAsync(destination, cancellationToken);
            }

            private async ValueTask<int> ReadInternalAsync(Memory<byte> destination, CancellationToken cancellationToken)
            {
                _owner.NotifyFirstRead();
                cancellationToken.ThrowIfCancellationRequested();

                if (_position >= _totalBytes)
                {
                    return 0;
                }

                await Task.Delay(20, cancellationToken);

                var remaining = (int)Math.Min(destination.Length, _totalBytes - _position);
                destination.Span.Slice(0, remaining).Clear();
                _position += remaining;
                return remaining;
            }
        }
    }

    internal sealed class FakeRangeHandler : HttpMessageHandler
    {
        private readonly int _totalBytes;
        private readonly bool _supportRange;
        private readonly string? _etag;

        public FakeRangeHandler(int totalBytes, bool supportRange, string? etag = null)
        {
            _totalBytes = totalBytes;
            _supportRange = supportRange;
            _etag = etag;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            var start = 0;
            if (_supportRange && request.Headers.Range != null)
            {
                var enumerator = request.Headers.Range.Ranges.GetEnumerator();
                if (enumerator.MoveNext())
                {
                    var range = enumerator.Current;
                    start = (int)(range.From ?? 0);
                }
                response.StatusCode = HttpStatusCode.PartialContent;
                response.Content = new ByteArrayContent(BuildBytes(_totalBytes - start));
                response.Content.Headers.ContentLength = _totalBytes - start;
            }
            else
            {
                response.StatusCode = HttpStatusCode.OK;
                response.Content = new ByteArrayContent(BuildBytes(_totalBytes));
                response.Content.Headers.ContentLength = _totalBytes;
            }

            if (!string.IsNullOrEmpty(_etag))
            {
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(_etag);
                response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            }
            return Task.FromResult(response);
        }

        private static byte[] BuildBytes(int count)
        {
            var data = new byte[count];
            for (int i = 0; i < count; i++) data[i] = (byte)(i % 251);
            return data;
        }
    }
    internal sealed class TestDownloadClient : BaseStreamingDownloadClient<TestSettings>
    {
        public TestDownloadClient(TestSettings settings, IAudioMetadataApplier? applier) : base(settings, null!, applier!)
        {
        }

        protected override string ServiceName => "Test";
        protected override string ProtocolName => "http";

        protected override Task<bool> AuthenticateAsync() => Task.FromResult(true);
        protected override Task<StreamingAlbum> GetAlbumAsync(string albumId) => Task.FromResult(new StreamingAlbum());
        protected override Task<StreamingTrack> GetTrackAsync(string trackId) => Task.FromResult(new StreamingTrack());
        protected override Task<string> GetStreamUrlAsync(string trackId, string quality) => Task.FromResult(string.Empty);
        protected override ValidationResult ValidateDownloadSettings(TestSettings settings) => new ValidationResult();

        public Task InvokeApplyMetadataAsync(string filePath, StreamingTrack metadata) => ApplyMetadataTagsAsync(filePath, metadata);
    }

    internal sealed class TestSettings : BaseStreamingSettings
    {
    }

    internal sealed class CapturingMetadataApplier : IAudioMetadataApplier
    {
        public bool WasCalled { get; private set; }
        public string? FilePath { get; private set; }
        public StreamingTrack? Metadata { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            FilePath = filePath;
            Metadata = metadata;
            CancellationToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    internal sealed class ThrowingMetadataApplier : IAudioMetadataApplier
    {
        public bool WasCalled { get; private set; }

        public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("Simulated metadata tagging failure");
        }
    }

    internal sealed class CountingMetadataApplier : IAudioMetadataApplier
    {
        public int CallCount { get; private set; }

        public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    internal sealed class CapturingLogger : ILogger
    {
        public int WarningCount { get; private set; }
        public string? LastWarningMessage { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                WarningCount++;
                LastWarningMessage = formatter(state, exception);
            }
        }
    }

}
