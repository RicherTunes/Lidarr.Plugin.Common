using System;
using System.Collections.Generic;
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
    /// <para>
    /// When the telemetry record carries the optional identity/quality enrichment fields
    /// (see <see cref="DownloadTelemetry.From"/>), the human line reads
    /// "Download completed: Artist - Track [FLAC HiRes 96kHz/24bit] 38.1MB in 4.00s (...) -> /path"
    /// instead of the IDs-only form, and the marker gains artist/track_title/album_title/format keys.
    /// Records constructed positionally (without enrichment) keep emitting the original IDs-only line.
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
                var sizeMb = telemetry.BytesWritten / 1048576.0;

                // Emit structured JSON marker for deterministic E2E detection.
                // This is the primary signal - E2E tests should look for this first.
                // The identity keys (artist/track_title/album_title/format) are additive: they are
                // always present (empty when the record was built without enrichment) and "success"
                // stays last so existing suffix matchers keep working.
                var structuredMarker = $"{StructuredMarkerPrefix} {{\"event\":\"telemetry_emitted\",\"service\":\"{EscapeJson(telemetry.ServiceName)}\",\"track\":\"{EscapeJson(telemetry.TrackId)}\",\"artist\":\"{EscapeJson(telemetry.Artist)}\",\"track_title\":\"{EscapeJson(telemetry.TrackTitle)}\",\"album_title\":\"{EscapeJson(telemetry.AlbumTitle)}\",\"format\":\"{EscapeJson(telemetry.Format)}\",\"success\":{(telemetry.Success ? "true" : "false")}}}";
                _logger.LogDebug("{StructuredMarker}", structuredMarker);

                var hasIdentity = !string.IsNullOrEmpty(telemetry.TrackTitle);

                if (telemetry.Success)
                {
                    if (hasIdentity)
                    {
                        _logger.LogInformation(
                            "Download completed: {Artist} - {TrackTitle} [{Quality}] {SizeMB:F1}MB in {ElapsedSeconds:F2}s ({Rate:F1} KB/s) -> {OutputPath} retries={RetryCount} 429s={TooManyRequestsCount}",
                            telemetry.Artist ?? "",
                            telemetry.TrackTitle,
                            FormatQuality(telemetry),
                            sizeMb,
                            seconds,
                            kbPerSecond,
                            telemetry.OutputPath ?? "",
                            telemetry.RetryCount,
                            telemetry.TooManyRequestsCount);
                    }
                    else
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
                }
                else
                {
                    if (hasIdentity)
                    {
                        _logger.LogWarning(
                            "Download failed: {Artist} - {TrackTitle} [{Quality}] after {ElapsedSeconds:F2}s retries={RetryCount} 429s={TooManyRequestsCount} error={ErrorMessage}",
                            telemetry.Artist ?? "",
                            telemetry.TrackTitle,
                            FormatQuality(telemetry),
                            seconds,
                            telemetry.RetryCount,
                            telemetry.TooManyRequestsCount,
                            telemetry.ErrorMessage ?? "");
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
            }
            catch
            {
                // best-effort; never break downloads for telemetry
            }
        }

        /// <summary>
        /// Builds a compact, human-friendly quality descriptor (e.g. "FLAC HiRes 1411kbps 96kHz/24bit"),
        /// omitting any field the plugin did not supply. Used only for the human log line.
        /// </summary>
        private static string FormatQuality(DownloadTelemetry t)
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(t.Format)) parts.Add(t.Format!);
            if (!string.IsNullOrEmpty(t.QualityTier)) parts.Add(t.QualityTier!);
            if (t.BitrateKbps is > 0) parts.Add($"{t.BitrateKbps}kbps");
            if (t.SampleRateHz is > 0)
            {
                var sampleRate = $"{t.SampleRateHz.Value / 1000.0:0.#}kHz";
                if (t.BitDepth is > 0) sampleRate += $"/{t.BitDepth}bit";
                parts.Add(sampleRate);
            }
            return string.Join(" ", parts);
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
