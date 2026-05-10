namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Outcome of a <see cref="CachingHttpExecutor"/> invocation, surfaced for telemetry hooks.
    /// </summary>
    public enum CacheHitKind
    {
        /// <summary>The response was served from a fresh (in-TTL) cache entry without contacting the origin.</summary>
        Hit,

        /// <summary>The cached entry was within the configured soft-revalidation window; served from cache without
        /// contacting the origin (a separate background revalidation may run elsewhere).</summary>
        SoftRevalidate,

        /// <summary>The origin returned 304 Not Modified and the cached body was folded into a synthetic 200 OK.</summary>
        NotModifiedFold,

        /// <summary>The origin returned a 5xx error and a sufficiently fresh cached body was served instead.</summary>
        StaleIfError,

        /// <summary>The cache entry (and any conditional validators) was evicted because the origin returned a
        /// terminal status (e.g., 404 or 410). The terminal response is returned to the caller as-is.</summary>
        EvictOnTerminal,

        /// <summary>The origin was contacted and returned a fresh successful response that was written to the cache.</summary>
        Miss,

        /// <summary>The origin was contacted and returned a non-cacheable response (e.g., non-success that did not
        /// trigger stale-if-error or terminal eviction). Returned to the caller without modification.</summary>
        Passthrough
    }
}
