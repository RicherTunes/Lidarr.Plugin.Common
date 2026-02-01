using System;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// Service for logging download telemetry information.
    /// Provides formatted logging for download performance and error tracking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This service emits two types of signals for each telemetry event:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <term>Structured JSON marker</term>
    /// <description>A deterministic JSON object with event type "telemetry_emitted" for machine parsing</description>
    /// </item>
    /// <item>
    /// <term>Human-readable log</term>
    /// <description>A formatted log message with key=value pairs for debugging</description>
    /// </item>
    /// </list>
    /// <para>
    /// The structured marker format is documented in <c>docs/TELEMETRY_DI_CONTRACT.md</c>.
    /// E2E tests should prefer the structured marker over regex-based log matching.
    /// </para>
    /// </remarks>
    public class DownloadTelemetryService : IDownloadTelemetryService
    {
        /// <summary>
        /// The structured marker prefix used for deterministic log parsing.
        /// Format: [LPC_TELEMETRY] {"event":"telemetry_emitted",...}
        /// </summary>
        /// <remarks>
        /// This marker is a stable contract for E2E tests. Changing it requires
        /// updating the telemetry DI gate in multi-plugin-docker-smoke-test.ps1.
        /// See docs/TELEMETRY_DI_CONTRACT.md for the full specification.
        /// </remarks>
        public const string StructuredMarkerPrefix = "[LPC_TELEMETRY]";

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

                // Emit structured JSON marker for deterministic E2E detection
                // This is the primary signal - E2E tests should look for this first
                // Format: [LPC_TELEMETRY] {"event":"telemetry_emitted","service":"...","success":true/false}
                var structuredMarker = $"{StructuredMarkerPrefix} {{\"event\":\"telemetry_emitted\",\"service\":\"{EscapeJson(telemetry.ServiceName)}\",\"track\":\"{EscapeJson(telemetry.TrackId)}\",\"success\":{(telemetry.Success ? "true" : "false")}}}";
                _logger.LogDebug("{StructuredMarker}", structuredMarker);

                // Emit human-readable log for debugging (existing behavior)
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

        /// <summary>
        /// Escapes a string for safe inclusion in JSON.
        /// </summary>
        private static string EscapeJson(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
    }
}
