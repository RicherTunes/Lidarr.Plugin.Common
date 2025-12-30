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
                getStreamAsync: (id, q) => Task.FromResult(("http://test/file", "bin"))
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
                getStreamAsync: (id, q) => Task.FromResult(("http://test/file2", "bin"))
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
                getStreamAsync: (id, q) => Task.FromResult(("http://slow/track", "bin"))
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
                getStreamAsync: (id, q) => Task.FromResult(("http://test/file", "bin"))
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

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 10 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string>()),
                getStreamAsync: (id, q) => Task.FromResult(("http://test/file", "bin"))
            );

            var dir = Path.Combine(Path.GetTempPath(), $"orch_test_album_empty_{Guid.NewGuid():N}");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", dir, new StreamingQuality { Bitrate = 320 });
                Assert.False(result.Success);
                Assert.Contains("No track IDs returned", result.ErrorMessage ?? string.Empty);
                Assert.Empty(result.FilePaths);
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
                getStreamAsync: (id, q) => Task.FromResult(("http://test/file", "bin")),
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
                getStreamAsync: (id, q) => Task.FromResult(("http://test/file", "bin")),
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
                getStreamAsync: (id, q) => Task.FromResult(("http://test/file", "bin")),
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
