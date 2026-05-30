using System;
using System.Collections.Generic;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Extensions;
using Lidarr.Plugin.Common.Services.Download;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Contract for the enriched download telemetry — the single source of truth that replaces
    /// qobuz's duplicated emoji logging and tidal's IDs-only line. The record now carries the
    /// user-valuable fields (artist/album/track/format/quality/size/path), a From(...) factory
    /// maps them off Common's StreamingTrack/StreamingQuality models, and the service emits one
    /// rich human line + the stable [LPC_TELEMETRY] marker.
    /// </summary>
    public class DownloadTelemetryEnrichmentTests
    {
        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<string> Messages { get; } = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));

            private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
        }

        private static StreamingTrack SampleTrack() => new()
        {
            Title = "So What",
            Artist = new StreamingArtist { Name = "Miles Davis" },
            Album = new StreamingAlbum { Title = "Kind of Blue" },
            Duration = TimeSpan.FromSeconds(545),
        };

        private static StreamingQuality SampleQuality() => new()
        {
            Format = "FLAC",
            Bitrate = 1411,
            SampleRate = 96000,
            BitDepth = 24,
        };

        [Fact]
        public void From_maps_track_album_quality_onto_record()
        {
            var t = SampleTrack();
            var q = SampleQuality();

            var rec = DownloadTelemetry.From(
                serviceName: "Qobuz", success: true, track: t, album: t.Album, quality: q,
                bytesWritten: 40_000_000, elapsed: TimeSpan.FromSeconds(4), outputPath: "/music/x.flac",
                retryCount: 1, tooManyRequestsCount: 0);

            Assert.Equal("Qobuz", rec.ServiceName);
            Assert.True(rec.Success);
            Assert.Equal("Miles Davis", rec.Artist);
            Assert.Equal("Kind of Blue", rec.AlbumTitle);
            Assert.Equal("So What", rec.TrackTitle);
            Assert.Equal("FLAC", rec.Format);
            Assert.Equal(StreamingQualityTier.HiRes.ToString(), rec.QualityTier);
            Assert.Equal(1411, rec.BitrateKbps);
            Assert.Equal(96000, rec.SampleRateHz);
            Assert.Equal(24, rec.BitDepth);
            Assert.Equal("/music/x.flac", rec.OutputPath);
            Assert.Equal(40_000_000, rec.BytesWritten);
            Assert.Equal(1, rec.RetryCount);
        }

        [Fact]
        public void LogDownloadTelemetry_emits_rich_human_line_when_fields_present()
        {
            var logger = new CapturingLogger<DownloadTelemetryService>();
            var svc = new DownloadTelemetryService(logger);

            var rec = DownloadTelemetry.From(
                "Qobuz", true, SampleTrack(), SampleTrack().Album, SampleQuality(),
                40_000_000, TimeSpan.FromSeconds(4), "/music/Kind of Blue/01 So What.flac");

            svc.LogDownloadTelemetry(rec);

            var joined = string.Join("\n", logger.Messages);
            Assert.Contains("Miles Davis", joined);
            Assert.Contains("So What", joined);
            Assert.Contains("FLAC", joined);
        }

        [Fact]
        public void LogDownloadTelemetry_marker_includes_identity_fields()
        {
            var logger = new CapturingLogger<DownloadTelemetryService>();
            var svc = new DownloadTelemetryService(logger);

            var rec = DownloadTelemetry.From(
                "Tidal", true, SampleTrack(), SampleTrack().Album, SampleQuality(),
                1000, TimeSpan.FromSeconds(1), "/x.flac");

            svc.LogDownloadTelemetry(rec);

            var marker = logger.Messages.Find(m => m.Contains(DownloadTelemetryService.StructuredMarkerPrefix));
            Assert.NotNull(marker);
            Assert.Contains("\"artist\":\"Miles Davis\"", marker);
            Assert.Contains("\"track_title\":\"So What\"", marker);
            Assert.Contains("\"format\":\"FLAC\"", marker);
        }

        [Fact]
        public void LogDownloadTelemetry_still_works_for_legacy_id_only_record()
        {
            var logger = new CapturingLogger<DownloadTelemetryService>();
            var svc = new DownloadTelemetryService(logger);

            // Back-compat: the original positional shape (no enrichment fields) must still log.
            var rec = new DownloadTelemetry("Apple", "alb1", "trk1", true, 2048, TimeSpan.FromSeconds(2), 1024, 0, 0, null);

            svc.LogDownloadTelemetry(rec);

            Assert.Contains(logger.Messages, m => m.Contains("trk1"));
        }

        private sealed class CapturingService : IDownloadTelemetryService
        {
            public List<DownloadTelemetry> Items { get; } = new();
            public void LogDownloadTelemetry(DownloadTelemetry telemetry) => Items.Add(telemetry);
        }

        [Fact]
        public void LoggingSink_delegates_to_telemetry_service()
        {
            // The canonical sink renders via the shared service rather than hand-rolling a format,
            // so a plugin that registers it gets identical logging with zero per-plugin code.
            var service = new CapturingService();
            var sink = new LoggingDownloadTelemetrySink(service);

            var rec = DownloadTelemetry.From("Qobuz", true, SampleTrack(), SampleTrack().Album, SampleQuality(), 1000, TimeSpan.FromSeconds(1));
            sink.OnTrackCompleted(rec);

            Assert.Same(rec, Assert.Single(service.Items));
        }

        [Fact]
        public void LoggingSink_logger_ctor_renders_rich_line()
        {
            var logger = new CapturingLogger<DownloadTelemetryService>();
            var sink = new LoggingDownloadTelemetrySink(logger);

            sink.OnTrackCompleted(DownloadTelemetry.From("Qobuz", true, SampleTrack(), SampleTrack().Album, SampleQuality(), 1000, TimeSpan.FromSeconds(1), "/x.flac"));

            Assert.Contains(logger.Messages, m => m.Contains("Miles Davis") && m.Contains("So What"));
        }

        [Fact]
        public void AddDownloadTelemetry_registers_service_and_logging_sink()
        {
            var services = new ServiceCollection();
            services.AddDownloadTelemetry();
            using var provider = services.BuildServiceProvider();

            Assert.IsType<DownloadTelemetryService>(provider.GetRequiredService<IDownloadTelemetryService>());
            Assert.IsType<LoggingDownloadTelemetrySink>(provider.GetRequiredService<IDownloadTelemetrySink>());
        }
    }
}
