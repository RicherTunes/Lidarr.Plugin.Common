using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Centralizes OpenTelemetry primitives for the library. No-ops when no listeners/subscribers are present.
    /// </summary>
    internal static class Observability
    {
        public static readonly ActivitySource Activity = new("Lidarr.Plugin.Common");

        internal static class Metrics
        {
            private static readonly Meter Meter = new("Lidarr.Plugin.Common");

            public static readonly Counter<long> CacheHit = Meter.CreateCounter<long>(
                name: "cache.hit",
                unit: null,
                description: "Number of cache hits.");

            public static readonly Counter<long> CacheMiss = Meter.CreateCounter<long>(
                name: "cache.miss",
                unit: null,
                description: "Number of cache misses.");

            public static readonly Counter<long> CacheRevalidate = Meter.CreateCounter<long>(
                name: "cache.revalidate",
                unit: null,
                description: "Number of cache revalidations (304 -> refreshed TTL). ");

            public static readonly Counter<long> RetryCount = Meter.CreateCounter<long>(
                name: "retry.count",
                unit: null,
                description: "Number of retries performed by resilience layer.");

            public static readonly Counter<long> AuthRefreshes = Meter.CreateCounter<long>(
                name: "auth.refreshes",
                unit: null,
                description: "Number of authentication/session refresh operations.");

#pragma warning disable IDE1006
            public static readonly Counter<long> ResilienceNonDI = Meter.CreateCounter<long>(
                name: "resilience.non_di",
                unit: null,
                description: "Count of non-DI resilience calls (builder without policy). Logged once per process via metric.");
#pragma warning restore IDE1006
            // Prefer UpDownCounter on NET8+, otherwise omit inflight tracking (callers should #if when using this).
#if NET8_0_OR_GREATER
            public static readonly UpDownCounter<long> RateLimiterInflight = Meter.CreateUpDownCounter<long>(
                name: "ratelimiter.inflight",
                unit: null,
                description: "Current in-flight requests per host (approximate). ");
#endif
        }
    }
}
