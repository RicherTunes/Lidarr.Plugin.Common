using Lidarr.Plugin.Common.Services.Download;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lidarr.Plugin.Common.Extensions
{
    /// <summary>
    /// DI helpers for opting into the shared download-telemetry logging path.
    /// </summary>
    public static class DownloadTelemetryServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the canonical download-telemetry services so a plugin gets ecosystem-standard
        /// per-track logging without writing its own sink: the shared
        /// <see cref="DownloadTelemetryService"/> (rich line + <c>[LPC_TELEMETRY]</c> marker) and the
        /// <see cref="LoggingDownloadTelemetrySink"/> that the download orchestrator drives. Both use
        /// TryAdd, so a plugin can still override either with a custom implementation if it has a
        /// genuine reason (and document that as intentionally divergent).
        /// </summary>
        public static IServiceCollection AddDownloadTelemetry(this IServiceCollection services)
        {
            services.TryAddSingleton<IDownloadTelemetryService, DownloadTelemetryService>();
            // Explicit factory rather than open-type registration: LoggingDownloadTelemetrySink has
            // two single-arg constructors, which would make the DI container's constructor selection
            // ambiguous. The factory pins the IDownloadTelemetryService-based one.
            services.TryAddSingleton<IDownloadTelemetrySink>(sp =>
                new LoggingDownloadTelemetrySink(sp.GetRequiredService<IDownloadTelemetryService>()));
            return services;
        }
    }
}
