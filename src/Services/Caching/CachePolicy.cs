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

            Name = name;
            ShouldCache = shouldCache;
            Duration = duration;
            SlidingExpiration = slidingExpiration;
            AbsoluteExpiration = absoluteExpiration;
            VaryByScope = false;
            SlidingRefreshWindow = slidingRefreshWindow;
            EnableConditionalRevalidation = enableConditionalRevalidation;
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
            : this(name, shouldCache, duration, slidingExpiration, absoluteExpiration, slidingRefreshWindow, enableConditionalRevalidation)
        {
            VaryByScope = varyByScope;
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
                enableConditionalRevalidation ?? EnableConditionalRevalidation);
        }
    }
}
