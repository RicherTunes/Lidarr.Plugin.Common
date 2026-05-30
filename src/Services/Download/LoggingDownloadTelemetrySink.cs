using System;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// The canonical <see cref="IDownloadTelemetrySink"/>: it renders every completed/failed track
    /// through the shared <see cref="IDownloadTelemetryService"/> (rich human line + the stable
    /// <c>[LPC_TELEMETRY]</c> marker). Plugins register this instead of hand-rolling a sink, so
    /// download logging is identical across the ecosystem and a format change touches exactly one
    /// file (<see cref="DownloadTelemetryService"/>). Best-effort: the underlying service never throws.
    /// </summary>
    public sealed class LoggingDownloadTelemetrySink : IDownloadTelemetrySink
    {
        private readonly IDownloadTelemetryService _service;

        /// <summary>Creates a sink that delegates to the supplied telemetry service.</summary>
        public LoggingDownloadTelemetrySink(IDownloadTelemetryService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        /// <summary>
        /// Convenience constructor for plugins that don't register an <see cref="IDownloadTelemetryService"/>
        /// in DI: wraps a fresh <see cref="DownloadTelemetryService"/> over the given logger.
        /// </summary>
        public LoggingDownloadTelemetrySink(ILogger<DownloadTelemetryService>? logger = null)
            : this(new DownloadTelemetryService(logger))
        {
        }

        /// <inheritdoc/>
        public void OnTrackCompleted(DownloadTelemetry telemetry) => _service.LogDownloadTelemetry(telemetry);
    }
}
