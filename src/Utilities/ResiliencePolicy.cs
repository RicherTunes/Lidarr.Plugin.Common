using System;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Describes the resilience characteristics used when executing outbound HTTP or HTTP-like requests.
    /// Immutable and thread-safe.
    /// </summary>
public sealed class ResiliencePolicy
    {
        public string Name { get; }
        public int MaxRetries { get; }
        public TimeSpan RetryBudget { get; }
        public int MaxConcurrencyPerHost { get; }
        /// <summary>
        /// Aggregate cap across all profiles for a given host. When null, falls back to <see cref="MaxConcurrencyPerHost"/>.
        /// </summary>
        public int MaxTotalConcurrencyPerHost { get; }
        public TimeSpan? PerRequestTimeout { get; }
        public TimeSpan InitialBackoff { get; }
        public TimeSpan MaxBackoff { get; }
        public TimeSpan JitterMin { get; }
        public TimeSpan JitterMax { get; }

        public static ResiliencePolicy Default { get; } = new ResiliencePolicy(
            name: "default",
            maxRetries: 5,
            retryBudget: TimeSpan.FromSeconds(60),
            maxConcurrencyPerHost: 6,
            perRequestTimeout: null,
            initialBackoff: TimeSpan.FromSeconds(2),
            maxBackoff: TimeSpan.FromSeconds(30),
            jitterMin: TimeSpan.FromMilliseconds(50),
            jitterMax: TimeSpan.FromMilliseconds(250));

        public static ResiliencePolicy Search { get; } = Default.With(
            name: "search",
            maxRetries: 4,
            retryBudget: TimeSpan.FromSeconds(20),
            maxConcurrencyPerHost: 4,
            perRequestTimeout: TimeSpan.FromSeconds(10),
            initialBackoff: TimeSpan.FromMilliseconds(400),
            maxBackoff: TimeSpan.FromSeconds(6));

        public static ResiliencePolicy Lookup { get; } = Default.With(
            name: "lookup",
            maxRetries: 5,
            retryBudget: TimeSpan.FromSeconds(40),
            maxConcurrencyPerHost: 5,
            perRequestTimeout: TimeSpan.FromSeconds(12),
            initialBackoff: TimeSpan.FromMilliseconds(600),
            maxBackoff: TimeSpan.FromSeconds(10));

        public static ResiliencePolicy Streaming { get; } = Default.With(
            name: "streaming",
            maxRetries: 3,
            retryBudget: TimeSpan.FromSeconds(45),
            maxConcurrencyPerHost: 3,
            perRequestTimeout: TimeSpan.FromSeconds(60),
            initialBackoff: TimeSpan.FromSeconds(1),
            maxBackoff: TimeSpan.FromSeconds(12));

        public static ResiliencePolicy Authentication { get; } = Default.With(
            name: "auth",
            maxRetries: 3,
            retryBudget: TimeSpan.FromSeconds(30),
            maxConcurrencyPerHost: 2,
            perRequestTimeout: TimeSpan.FromSeconds(15),
            initialBackoff: TimeSpan.FromMilliseconds(500),
            maxBackoff: TimeSpan.FromSeconds(8));

        public static ResiliencePolicy Metadata { get; } = Default.With(
            name: "metadata",
            maxRetries: 4,
            retryBudget: TimeSpan.FromSeconds(25),
            maxConcurrencyPerHost: 4,
            perRequestTimeout: TimeSpan.FromSeconds(10),
            initialBackoff: TimeSpan.FromMilliseconds(500),
            maxBackoff: TimeSpan.FromSeconds(8));

        public ResiliencePolicy(
            string name,
            int maxRetries,
            TimeSpan retryBudget,
            int maxConcurrencyPerHost,
            TimeSpan? perRequestTimeout,
            TimeSpan initialBackoff,
            TimeSpan maxBackoff,
            TimeSpan? jitterMin = null,
            TimeSpan? jitterMax = null,
            int? maxTotalConcurrencyPerHost = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Policy name cannot be null or whitespace.", nameof(name));
            }

            if (maxRetries < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "Max retries must be at least 1.");
            }

            if (retryBudget <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retryBudget), retryBudget, "Retry budget must be positive.");
            }

            if (maxConcurrencyPerHost < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxConcurrencyPerHost), maxConcurrencyPerHost, "Concurrency must be at least 1.");
            }

            if (initialBackoff <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(initialBackoff), initialBackoff, "Initial backoff must be positive.");
            }

            if (maxBackoff < initialBackoff)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBackoff), maxBackoff, "Max backoff must be greater than or equal to the initial backoff.");
            }

            TimeSpan jitterMinimum = jitterMin ?? TimeSpan.FromMilliseconds(50);
            TimeSpan jitterMaximum = jitterMax ?? TimeSpan.FromMilliseconds(250);

            if (jitterMinimum < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(jitterMin), jitterMinimum, "Minimum jitter cannot be negative.");
            }

            if (jitterMaximum < jitterMinimum)
            {
                throw new ArgumentOutOfRangeException(nameof(jitterMax), jitterMaximum, "Maximum jitter must be >= minimum jitter.");
            }

            Name = name;
            MaxRetries = maxRetries;
            RetryBudget = retryBudget;
            MaxConcurrencyPerHost = maxConcurrencyPerHost;
            MaxTotalConcurrencyPerHost = maxTotalConcurrencyPerHost ?? maxConcurrencyPerHost;
            PerRequestTimeout = perRequestTimeout;
            InitialBackoff = initialBackoff;
            MaxBackoff = maxBackoff;
            JitterMin = jitterMinimum;
            JitterMax = jitterMaximum;
        }

        public ResiliencePolicy With(
            string? name = null,
            int? maxRetries = null,
            TimeSpan? retryBudget = null,
            int? maxConcurrencyPerHost = null,
            TimeSpan? perRequestTimeout = null,
            TimeSpan? initialBackoff = null,
            TimeSpan? maxBackoff = null,
            TimeSpan? jitterMin = null,
            TimeSpan? jitterMax = null,
            int? maxTotalConcurrencyPerHost = null)
        {
            return new ResiliencePolicy(
                name ?? Name,
                maxRetries ?? MaxRetries,
                retryBudget ?? RetryBudget,
                maxConcurrencyPerHost ?? MaxConcurrencyPerHost,
                perRequestTimeout ?? PerRequestTimeout,
                initialBackoff ?? InitialBackoff,
                maxBackoff ?? MaxBackoff,
                jitterMin ?? JitterMin,
                jitterMax ?? JitterMax,
                maxTotalConcurrencyPerHost ?? MaxTotalConcurrencyPerHost);
        }

        internal TimeSpan ComputeDelay(int attempt)
        {
            if (attempt < 1)
            {
                attempt = 1;
            }

            // Backoff grows exponentially but is capped by MaxBackoff.
            var multiplier = Math.Pow(2, attempt - 1);
            var proposed = TimeSpan.FromMilliseconds(InitialBackoff.TotalMilliseconds * multiplier);
            return proposed <= MaxBackoff ? proposed : MaxBackoff;
        }

        internal TimeSpan ComputeJitter()
        {
            if (JitterMax == TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var minMs = (int)JitterMin.TotalMilliseconds;
            var maxMs = (int)JitterMax.TotalMilliseconds;
            if (maxMs <= minMs)
            {
                return TimeSpan.FromMilliseconds(minMs);
            }

            var jitterMs = RandomProvider.Next(minMs, maxMs + 1);
            return TimeSpan.FromMilliseconds(jitterMs);
        }
    }
}
