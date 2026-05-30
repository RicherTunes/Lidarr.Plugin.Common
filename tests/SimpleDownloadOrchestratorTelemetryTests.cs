using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class SimpleDownloadOrchestratorTelemetryTests
    {
        [Fact]
        public async Task Should_emit_track_telemetry_on_success()
        {
            var sink = new CapturingTelemetrySink();
            using var httpClient = new HttpClient(new OkBytesHandler(bytes: 1024));

            var outputDir = Path.Combine(Path.GetTempPath(), $"orch_telemetry_{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, "track");

            try
            {
                var orchestrator = new SimpleDownloadOrchestrator(
                    serviceName: "Test",
                    httpClient: httpClient,
                    getAlbumAsync: _ => Task.FromResult(new StreamingAlbum()),
                    getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", TrackNumber = 1 }),
                    getAlbumTrackIdsAsync: _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
                    getStreamAsync: (_, _) => Task.FromResult<(string Url, string Extension)>(("https://unit.test/stream", "bin")),
                    maxConcurrentTracks: 1,
                    streamProvider: null,
                    metadataApplier: new NoopMetadataApplier(),
                    logger: null,
                    postProcessor: null,
                    telemetrySink: sink);

                var result = await orchestrator.DownloadTrackAsync("track1", outputPath, quality: null, CancellationToken.None);

                Assert.True(result.Success);
                Assert.NotNull(result.FilePath);
                Assert.True(File.Exists(result.FilePath));

                var telemetry = Assert.Single(sink.Items);
                Assert.Equal("Test", telemetry.ServiceName);
                Assert.Null(telemetry.AlbumId);
                Assert.Equal("track1", telemetry.TrackId);
                Assert.True(telemetry.Success);
                Assert.Equal(1024, telemetry.BytesWritten);
                Assert.Equal(0, telemetry.RetryCount);
                Assert.Equal(0, telemetry.TooManyRequestsCount);
            }
            finally
            {
                try { Directory.Delete(outputDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task Should_count_retries_and_429s_in_track_telemetry()
        {
            var sink = new CapturingTelemetrySink();
            using var httpClient = new HttpClient(new RetryThenOkHandler());

            var outputDir = Path.Combine(Path.GetTempPath(), $"orch_telemetry_retry_{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, "track");

            try
            {
                var orchestrator = new SimpleDownloadOrchestrator(
                    serviceName: "Test",
                    httpClient: httpClient,
                    getAlbumAsync: _ => Task.FromResult(new StreamingAlbum()),
                    getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", TrackNumber = 1 }),
                    getAlbumTrackIdsAsync: _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
                    getStreamAsync: (_, _) => Task.FromResult<(string Url, string Extension)>(("https://unit.test/stream", "bin")),
                    maxConcurrentTracks: 1,
                    streamProvider: null,
                    metadataApplier: new NoopMetadataApplier(),
                    logger: null,
                    postProcessor: null,
                    telemetrySink: sink);

                var result = await orchestrator.DownloadTrackAsync("track1", outputPath, quality: null, CancellationToken.None);

                Assert.True(result.Success);

                var telemetry = Assert.Single(sink.Items);
                Assert.Equal(1, telemetry.RetryCount);
                Assert.Equal(1, telemetry.TooManyRequestsCount);
                Assert.True(telemetry.BytesWritten > 0);
            }
            finally
            {
                try { Directory.Delete(outputDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task Should_emit_enriched_track_telemetry_with_identity_and_quality()
        {
            // The orchestrator is the single producer of download telemetry. It must enrich the
            // emitted record from the StreamingTrack/StreamingQuality it already has in scope, so
            // every plugin's sink receives artist/album/track/format/quality without re-deriving them.
            var sink = new CapturingTelemetrySink();
            using var httpClient = new HttpClient(new OkBytesHandler(bytes: 4096));

            var outputDir = Path.Combine(Path.GetTempPath(), $"orch_telemetry_rich_{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, "track");

            try
            {
                var orchestrator = new SimpleDownloadOrchestrator(
                    serviceName: "Test",
                    httpClient: httpClient,
                    getAlbumAsync: _ => Task.FromResult(new StreamingAlbum { Id = "alb1", Title = "Kind of Blue" }),
                    getTrackAsync: id => Task.FromResult(new StreamingTrack
                    {
                        Id = id,
                        Title = "So What",
                        TrackNumber = 1,
                        Artist = new StreamingArtist { Name = "Miles Davis" },
                        Album = new StreamingAlbum { Id = "alb1", Title = "Kind of Blue" },
                        Duration = TimeSpan.FromSeconds(545),
                    }),
                    getAlbumTrackIdsAsync: _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
                    getStreamAsync: (_, _) => Task.FromResult<(string Url, string Extension)>(("https://unit.test/stream", "flac")),
                    maxConcurrentTracks: 1,
                    streamProvider: null,
                    metadataApplier: new NoopMetadataApplier(),
                    logger: null,
                    postProcessor: null,
                    telemetrySink: sink);

                var quality = new StreamingQuality { Format = "FLAC", Bitrate = 1411, SampleRate = 96000, BitDepth = 24 };
                var result = await orchestrator.DownloadTrackAsync("track1", outputPath, quality, CancellationToken.None);

                Assert.True(result.Success);

                var telemetry = Assert.Single(sink.Items);
                Assert.Equal("track1", telemetry.TrackId);     // authoritative id preserved (not track.Id from model)
                Assert.Equal("Miles Davis", telemetry.Artist);
                Assert.Equal("So What", telemetry.TrackTitle);
                Assert.Equal("Kind of Blue", telemetry.AlbumTitle);
                Assert.Equal("FLAC", telemetry.Format);
                Assert.Equal(StreamingQualityTier.HiRes.ToString(), telemetry.QualityTier);
                Assert.Equal(96000, telemetry.SampleRateHz);
                Assert.False(string.IsNullOrEmpty(telemetry.OutputPath));
            }
            finally
            {
                try { Directory.Delete(outputDir, recursive: true); } catch { }
            }
        }

        internal sealed class CapturingTelemetrySink : IDownloadTelemetrySink
        {
            private readonly object _lock = new();
            private readonly List<DownloadTelemetry> _items = new();

            public IReadOnlyList<DownloadTelemetry> Items
            {
                get
                {
                    lock (_lock) return _items.ToArray();
                }
            }

            public void OnTrackCompleted(DownloadTelemetry telemetry)
            {
                lock (_lock) _items.Add(telemetry);
            }
        }

        internal sealed class NoopMetadataApplier : IAudioMetadataApplier
        {
            public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        internal sealed class OkBytesHandler : HttpMessageHandler
        {
            private readonly int _bytes;

            public OkBytesHandler(int bytes)
            {
                _bytes = bytes;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var payload = new byte[_bytes];
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                response.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(response);
            }
        }

        internal sealed class RetryThenOkHandler : HttpMessageHandler
        {
            private int _attempt;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var attempt = Interlocked.Increment(ref _attempt);
                if (attempt == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    };
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                    return Task.FromResult(response);
                }

                var payload = new byte[2048];
                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                ok.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(ok);
            }
        }
    }
}
