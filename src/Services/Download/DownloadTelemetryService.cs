using System;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// Service for logging download telemetry information.
    /// Provides formatted logging for download performance and error tracking.
    /// </summary>
    public class DownloadTelemetryService : IDownloadTelemetryService
    {
        private readonly ILogger<DownloadTelemetryService> _logger;

        /// <summary>
        /// Creates a new DownloadTelemetryService.
        /// </summary>
        /// <param name="logger">Optional logger for telemetry output</param>
        public DownloadTelemetryService(ILogger<DownloadTelemetryService>? logger = null)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DownloadTelemetryService>.Instance;
        }

        /// <inheritdoc/>
        public void LogDownloadTelemetry(DownloadTelemetry telemetry)
        {
            try
            {
                var seconds = Math.Max(0.001, telemetry.Elapsed.TotalSeconds);
                var kbPerSecond = (telemetry.BytesPerSecond / 1024.0);

                if (telemetry.Success)
                {
                    _logger.LogInformation(
                        "Download completed: track={TrackId} album={AlbumId} bytes={BytesWritten} elapsed={ElapsedSeconds:F2}s rate={Rate:F1}KB/s retries={RetryCount} 429s={TooManyRequestsCount}",
                        telemetry.TrackId,
                        telemetry.AlbumId ?? "",
                        telemetry.BytesWritten,
                        seconds,
                        kbPerSecond,
                        telemetry.RetryCount,
                        telemetry.TooManyRequestsCount);
                }
                else
                {
                    _logger.LogWarning(
                        "Download failed: track={TrackId} album={AlbumId} elapsed={ElapsedSeconds:F2}s retries={RetryCount} 429s={TooManyRequestsCount} error={ErrorMessage}",
                        telemetry.TrackId,
                        telemetry.AlbumId ?? "",
                        seconds,
                        telemetry.RetryCount,
                        telemetry.TooManyRequestsCount,
                        telemetry.ErrorMessage ?? "");
                }
            }
            catch
            {
                // best-effort; never break downloads for telemetry
            }
        }
    }
}
