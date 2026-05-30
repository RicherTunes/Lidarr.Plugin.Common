using System;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// Canonical per-track download telemetry record shared by all plugins. The leading fields are
    /// the original transfer facts; the trailing nullable fields carry the user-valuable identity
    /// and quality info (artist/album/track/format/quality/path) so the logged line reads
    /// "Miles Davis - So What [FLAC HiRes] ... -> /path" instead of "track=123 album=456". The
    /// enrichment fields default to null, preserving the original positional construction.
    /// </summary>
    public sealed record DownloadTelemetry(
        string ServiceName,
        string? AlbumId,
        string TrackId,
        bool Success,
        long BytesWritten,
        TimeSpan Elapsed,
        double BytesPerSecond,
        int RetryCount,
        int TooManyRequestsCount,
        string? ErrorMessage,
        string? Artist = null,
        string? AlbumTitle = null,
        string? TrackTitle = null,
        string? Format = null,
        string? QualityTier = null,
        int? BitrateKbps = null,
        int? SampleRateHz = null,
        int? BitDepth = null,
        TimeSpan? TrackDuration = null,
        string? OutputPath = null)
    {
        /// <summary>
        /// Builds an enriched telemetry record from Common's streaming models. Plugins supply only
        /// the service-specific <see cref="StreamingTrack"/>/<see cref="StreamingAlbum"/>/
        /// <see cref="StreamingQuality"/> they already construct plus the transfer facts; the
        /// identity/quality fields are mapped here so every plugin logs the same shape.
        /// </summary>
        public static DownloadTelemetry From(
            string serviceName,
            bool success,
            StreamingTrack? track,
            StreamingAlbum? album,
            StreamingQuality? quality,
            long bytesWritten,
            TimeSpan elapsed,
            string? outputPath = null,
            int retryCount = 0,
            int tooManyRequestsCount = 0,
            string? errorMessage = null)
        {
            var bytesPerSecond = elapsed.TotalSeconds > 0 ? bytesWritten / elapsed.TotalSeconds : 0d;
            return new DownloadTelemetry(
                ServiceName: serviceName,
                AlbumId: album?.Id,
                TrackId: track?.Id ?? string.Empty,
                Success: success,
                BytesWritten: bytesWritten,
                Elapsed: elapsed,
                BytesPerSecond: bytesPerSecond,
                RetryCount: retryCount,
                TooManyRequestsCount: tooManyRequestsCount,
                ErrorMessage: errorMessage,
                Artist: string.IsNullOrWhiteSpace(track?.Artist?.Name) ? null : track!.Artist.Name,
                AlbumTitle: album?.Title ?? track?.Album?.Title,
                TrackTitle: string.IsNullOrWhiteSpace(track?.Title) ? null : track!.Title,
                Format: string.IsNullOrWhiteSpace(quality?.Format) ? null : quality!.Format,
                QualityTier: quality?.GetTier().ToString(),
                BitrateKbps: quality?.Bitrate,
                SampleRateHz: quality?.SampleRate,
                BitDepth: quality?.BitDepth,
                TrackDuration: track?.Duration,
                OutputPath: outputPath);
        }
    }
}
