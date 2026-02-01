using System;

namespace Lidarr.Plugin.Common.Services.Download
{
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
        string? ErrorMessage);
}

