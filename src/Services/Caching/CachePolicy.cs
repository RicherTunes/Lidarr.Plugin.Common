using System;

namespace Lidarr.Plugin.Common.Services.Caching
{
    /// <summary>
    /// Declarative cache policy describing whether and how long responses should be cached.
    /// Immutable and thread-safe.
    /// </summary>
public sealed class CachePolicy
    {
        public string Name { get; }
        public bool ShouldCache { get; }
        public TimeSpan Duration { get; }
        public TimeSpan? SlidingExpiration { get; }
        public TimeSpan? AbsoluteExpiration { get; }
        /// <summary>
        /// When true, caches should vary lookups by a caller-provided scope (e.g., user/tenant).
        /// </summary>
        public bool VaryByScope { get; }
        /// <summary>
        /// Optional coalescing window for sliding expiration updates. When set, the cache will
        /// extend TTL at most once per this window to avoid stampedes.
        /// </summary>
        public TimeSpan? SlidingRefreshWindow { get; }
        /// <summary>
        /// When true, attach conditional validators (ETag/Last-Modified) from cached entries automatically
        /// to enable 304 revalidation without external state.
        /// </summary>
        public bool EnableConditionalRevalidation { get; }

        /// <summary>
        /// Soft-revalidation budget consumed by <see cref="Lidarr.Plugin.Common.Services.Http.CachingHttpExecutor"/>.
        /// When set, a cached body that is younger than this window is returned without contacting the origin
        /// (allowing the caller to revalidate later). Mirrors the
        /// <c>APPLEMUSICARR_SOFT_REVALIDATE_DAYS</c> env override that previously existed in plugins.
        /// </summary>
        public TimeSpan? SoftRevalidateWindow { get; }

        /// <summary>
        /// Stale-if-error budget consumed by <see cref="Lidarr.Plugin.Common.Services.Http.CachingHttpExecutor"/>.
        /// When set, a cached body that is younger than this window is returned (with a Warning header) when
        /// the origin returns a 5xx response. Defaults to <see langword="null"/> (disabled). When the origin
        /// path supports stale-if-error, plugins typically set this to 7 days (the previous
        /// <c>APPLEMUSICARR_STALE_IF_ERROR_DAYS</c> default).
        /// </summary>
        public TimeSpan? StaleIfErrorTtl { get; }

        /// <summary>
        /// When <see langword="true"/>, <see cref="Lidarr.Plugin.Common.Services.Http.CachingHttpExecutor"/>
        /// evicts the cached body and any conditional validators on terminal origin statuses (404 or 410), to
        /// avoid repeatedly hitting the origin with conditional requests for resources known to be missing.
        /// Default <see langword="true"/>.
        /// </summary>
        public bool EvictOnTerminalStatus { get; }

        public static CachePolicy Disabled { get; } = new CachePolicy(
            name: "disabled",
            shouldCache: false,
            duration: TimeSpan.Zero);

        public static CachePolicy Default { get; } = new CachePolicy(
            name: "default",
            shouldCache: true,
            duration: TimeSpan.FromMinutes(15));

        public static CachePolicy ShortLived { get; } = Default.With(
            name: "short-lived",
            duration: TimeSpan.FromMinutes(2));

        public static CachePolicy MediumLived { get; } = Default.With(
            name: "medium-lived",
            duration: TimeSpan.FromMinutes(10));

        public static CachePolicy LongLived { get; } = Default.With(
            name: "long-lived",
            duration: TimeSpan.FromHours(6));

        public CachePolicy(
            string name,
            bool shouldCache,
            TimeSpan duration,
            TimeSpan? slidingExpiration = null,
            TimeSpan? absoluteExpiration = null,
            TimeSpan? slidingRefreshWindow = null,
            bool enableConditionalRevalidation = false)
            : this(
                name,
                shouldCache,
                duration,
                slidingExpiration,
                absoluteExpiration,
                varyByScope: false,
                slidingRefreshWindow,
                enableConditionalRevalidation,
                softRevalidateWindow: null,
                staleIfErrorTtl: null,
                evictOnTerminalStatus: true)
        {
        }

        /// <summary>
        /// Extended constructor allowing explicit configuration of <see cref="VaryByScope"/>.
        /// </summary>
        public CachePolicy(
            string name,
            bool shouldCache,
            TimeSpan duration,
            TimeSpan? slidingExpiration,
            TimeSpan? absoluteExpiration,
            bool varyByScope,
            TimeSpan? slidingRefreshWindow = null,
            bool enableConditionalRevalidation = false)
            : this(
                name,
                shouldCache,
                duration,
                slidingExpiration,
                absoluteExpiration,
                varyByScope,
                slidingRefreshWindow,
                enableConditionalRevalidation,
                softRevalidateWindow: null,
                staleIfErrorTtl: null,
                evictOnTerminalStatus: true)
        {
        }

        /// <summary>
        /// Full constructor that also controls the <see cref="Lidarr.Plugin.Common.Services.Http.CachingHttpExecutor"/>
        /// soft-revalidate, stale-if-error and terminal-eviction knobs.
        /// </summary>
        public CachePolicy(
            string name,
            bool shouldCache,
            TimeSpan duration,
            TimeSpan? slidingExpiration,
            TimeSpan? absoluteExpiration,
            bool varyByScope,
            TimeSpan? slidingRefreshWindow,
            bool enableConditionalRevalidation,
            TimeSpan? softRevalidateWindow,
            TimeSpan? staleIfErrorTtl,
            bool evictOnTerminalStatus)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Policy name cannot be null or whitespace.", nameof(name));
            }

            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration cannot be negative.");
            }

            if (slidingExpiration.HasValue && slidingExpiration.Value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(slidingExpiration), slidingExpiration, "Sliding expiration cannot be negative.");
            }

            if (absoluteExpiration.HasValue && absoluteExpiration.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(absoluteExpiration), absoluteExpiration, "Absolute expiration must be positive.");
            }

            if (softRevalidateWindow.HasValue && softRevalidateWindow.Value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(softRevalidateWindow), softRevalidateWindow, "Soft revalidate window cannot be negative.");
            }

            if (staleIfErrorTtl.HasValue && staleIfErrorTtl.Value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(staleIfErrorTtl), staleIfErrorTtl, "Stale-if-error TTL cannot be negative.");
            }

            Name = name;
            ShouldCache = shouldCache;
            Duration = duration;
            SlidingExpiration = slidingExpiration;
            AbsoluteExpiration = absoluteExpiration;
            VaryByScope = varyByScope;
            SlidingRefreshWindow = slidingRefreshWindow;
            EnableConditionalRevalidation = enableConditionalRevalidation;
            SoftRevalidateWindow = softRevalidateWindow;
            StaleIfErrorTtl = staleIfErrorTtl;
            EvictOnTerminalStatus = evictOnTerminalStatus;
        }

        /// <summary>
        /// Creates a new policy based on this one, optionally overriding properties. Supports <see cref="VaryByScope"/>.
        /// </summary>
        public CachePolicy With(
            string? name = null,
            bool? shouldCache = null,
            TimeSpan? duration = null,
            TimeSpan? slidingExpiration = null,
            TimeSpan? absoluteExpiration = null,
            bool? varyByScope = null,
            TimeSpan? slidingRefreshWindow = null,
            bool? enableConditionalRevalidation = null)
        {
            return new CachePolicy(
                name ?? Name,
                shouldCache ?? ShouldCache,
                duration ?? Duration,
                slidingExpiration ?? SlidingExpiration,
                absoluteExpiration ?? AbsoluteExpiration,
                varyByScope ?? VaryByScope,
                slidingRefreshWindow ?? SlidingRefreshWindow,
                enableConditionalRevalidation ?? EnableConditionalRevalidation,
                SoftRevalidateWindow,
                StaleIfErrorTtl,
                EvictOnTerminalStatus);
        }

        /// <summary>
        /// Extended <c>With</c> overload that exposes the <see cref="Lidarr.Plugin.Common.Services.Http.CachingHttpExecutor"/>
        /// knobs alongside the existing parameters. Existing call sites that use the original <see cref="With(string?, bool?, System.Nullable{System.TimeSpan}, System.Nullable{System.TimeSpan}, System.Nullable{System.TimeSpan}, bool?, System.Nullable{System.TimeSpan}, bool?)"/>
        /// continue to compile and behave identically.
        /// </summary>
        public CachePolicy WithExecutor(
            TimeSpan? softRevalidateWindow = null,
            TimeSpan? staleIfErrorTtl = null,
            bool? evictOnTerminalStatus = null)
        {
            return new CachePolicy(
                Name,
                ShouldCache,
                Duration,
                SlidingExpiration,
                AbsoluteExpiration,
                VaryByScope,
                SlidingRefreshWindow,
                EnableConditionalRevalidation,
                softRevalidateWindow ?? SoftRevalidateWindow,
                staleIfErrorTtl ?? StaleIfErrorTtl,
                evictOnTerminalStatus ?? EvictOnTerminalStatus);
        }
    }
}
