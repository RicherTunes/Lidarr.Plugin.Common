using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// Interface for a simple token bucket rate limiter.
    /// Provides straightforward rate limiting for resources with fixed limits.
    /// </summary>
    /// <remarks>
    /// Use this for:
    /// - Simple, fixed rate limits (e.g., "10 requests per minute")
    /// - Resources where you know the exact rate limit
    /// - Local services with predictable capacity
    ///
    /// For adaptive rate limiting that learns from responses, use
    /// <see cref="Performance.IUniversalAdaptiveRateLimiter"/> instead.
    /// </remarks>
    public interface ITokenBucketRateLimiter
    {
        /// <summary>
        /// Executes an operation with rate limiting, waiting if necessary.
        /// </summary>
        /// <typeparam name="T">Return type of the operation.</typeparam>
        /// <param name="resource">Resource identifier for rate limiting.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <returns>Result of the operation.</returns>
        Task<T> ExecuteAsync<T>(string resource, Func<Task<T>> operation);

        /// <summary>
        /// Executes an operation with rate limiting and cancellation support.
        /// </summary>
        /// <typeparam name="T">Return type of the operation.</typeparam>
        /// <param name="resource">Resource identifier for rate limiting.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Result of the operation.</returns>
        Task<T> ExecuteAsync<T>(string resource, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);

        /// <summary>
        /// Configures rate limiting for a specific resource.
        /// </summary>
        /// <param name="resource">Resource identifier.</param>
        /// <param name="maxRequests">Maximum requests allowed in the period.</param>
        /// <param name="period">Time period for the rate limit.</param>
        void Configure(string resource, int maxRequests, TimeSpan period);

        /// <summary>
        /// Gets the current available tokens for a resource.
        /// </summary>
        /// <param name="resource">Resource identifier.</param>
        /// <returns>Number of available tokens, or null if resource not configured.</returns>
        int? GetAvailableTokens(string resource);

        /// <summary>
        /// Resets the rate limiter for a specific resource or all resources.
        /// </summary>
        /// <param name="resource">Resource to reset, or null to reset all.</param>
        void Reset(string resource = null);
    }

    /// <summary>
    /// Simple token bucket rate limiter implementation.
    /// </summary>
    /// <remarks>
    /// Token Bucket Algorithm:
    /// - Tokens are added at a fixed rate (refill rate = capacity / period)
    /// - Each request consumes one token
    /// - If no tokens available, request waits until tokens refill
    /// - Maximum tokens capped at capacity
    ///
    /// Example: 10 requests per minute
    /// - Capacity: 10 tokens
    /// - Refill rate: 10/60 = 0.167 tokens per second
    /// - Burst: Can handle 10 requests immediately
    /// - Sustained: ~0.167 requests per second
    /// </remarks>
    public class TokenBucketRateLimiter : ITokenBucketRateLimiter
    {
        private readonly ConcurrentDictionary<string, TokenBucket> _buckets;
        private readonly ILogger _logger;

        /// <summary>
        /// Creates a new token bucket rate limiter.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public TokenBucketRateLimiter(ILogger logger = null)
        {
            _buckets = new ConcurrentDictionary<string, TokenBucket>(StringComparer.OrdinalIgnoreCase);
            _logger = logger;
        }

        /// <inheritdoc />
        public void Configure(string resource, int maxRequests, TimeSpan period)
        {
            if (string.IsNullOrWhiteSpace(resource))
            {
                _logger?.LogWarning("Rate limiter resource name cannot be empty, using 'default'");
                resource = "default";
            }

            if (maxRequests <= 0)
            {
                _logger?.LogWarning("Invalid maxRequests ({MaxRequests}), using default value of 10", maxRequests);
                maxRequests = 10;
            }

            if (period <= TimeSpan.Zero)
            {
                _logger?.LogWarning("Invalid period ({Period}), using default value of 1 minute", period);
                period = TimeSpan.FromMinutes(1);
            }

            _buckets[resource] = new TokenBucket(maxRequests, period);
            _logger?.LogDebug("Rate limiter configured for {Resource}: {MaxRequests} requests per {PeriodSeconds}s",
                resource, maxRequests, period.TotalSeconds);
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(string resource, Func<Task<T>> operation)
        {
            return await ExecuteAsync(resource, _ => operation(), CancellationToken.None).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(string resource, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            // If no limiter configured for resource, execute without rate limiting
            if (!_buckets.TryGetValue(resource, out var bucket))
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }

            var waitTime = bucket.ReserveToken();
            if (waitTime > TimeSpan.Zero)
            {
                _logger?.LogDebug("Rate limit for {Resource}: waiting {WaitMs:F0}ms", resource, waitTime.TotalMilliseconds);
                await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public int? GetAvailableTokens(string resource)
        {
            if (_buckets.TryGetValue(resource, out var bucket))
            {
                return bucket.GetAvailableTokens();
            }
            return null;
        }

        /// <inheritdoc />
        public void Reset(string resource = null)
        {
            if (resource != null)
            {
                if (_buckets.TryGetValue(resource, out var bucket))
                {
                    bucket.Reset();
                    _logger?.LogDebug("Reset rate limiter for {Resource}", resource);
                }
            }
            else
            {
                foreach (var bucket in _buckets.Values)
                {
                    bucket.Reset();
                }
                _logger?.LogDebug("Reset all rate limiters");
            }
        }

        /// <summary>
        /// Internal token bucket implementation.
        /// </summary>
        private class TokenBucket
        {
            private readonly object _lock = new object();
            private readonly double _maxTokens;
            private readonly double _refillRatePerSecond;
            private double _availableTokens;
            private DateTime _lastRefill;

            public TokenBucket(int maxRequests, TimeSpan period)
            {
                _maxTokens = Math.Max(1, maxRequests);
                var seconds = Math.Max(0.001, period.TotalSeconds);
                _refillRatePerSecond = _maxTokens / seconds;
                _availableTokens = _maxTokens;
                _lastRefill = DateTime.UtcNow;
            }

            public TimeSpan ReserveToken()
            {
                lock (_lock)
                {
                    RefillTokens();

                    if (_availableTokens >= 1.0)
                    {
                        _availableTokens -= 1.0;
                        return TimeSpan.Zero;
                    }

                    // Calculate wait time for 1 token
                    var tokensNeeded = 1.0 - _availableTokens;
                    var secondsToWait = Math.Max(0, tokensNeeded / _refillRatePerSecond);
                    _availableTokens -= 1.0; // Reserve the token (goes negative)
                    return TimeSpan.FromSeconds(secondsToWait);
                }
            }

            public int GetAvailableTokens()
            {
                lock (_lock)
                {
                    RefillTokens();
                    return (int)Math.Max(0, _availableTokens);
                }
            }

            public void Reset()
            {
                lock (_lock)
                {
                    _availableTokens = _maxTokens;
                    _lastRefill = DateTime.UtcNow;
                }
            }

            private void RefillTokens()
            {
                var now = DateTime.UtcNow;
                if (now <= _lastRefill)
                    return;

                var elapsedSeconds = (now - _lastRefill).TotalSeconds;
                if (elapsedSeconds <= 0)
                    return;

                _availableTokens = Math.Min(_maxTokens, _availableTokens + elapsedSeconds * _refillRatePerSecond);
                _lastRefill = now;
            }
        }
    }

    /// <summary>
    /// Common rate limit configurations for various service types.
    /// </summary>
    public static class RateLimitPresets
    {
        /// <summary>
        /// Configures rate limits for local AI providers (Ollama, LM Studio).
        /// Higher limits since they run locally.
        /// </summary>
        public static void ConfigureLocalAI(ITokenBucketRateLimiter limiter)
        {
            limiter.Configure("ollama", 30, TimeSpan.FromMinutes(1));
            limiter.Configure("lmstudio", 30, TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// Configures rate limits for cloud AI providers.
        /// More conservative to avoid hitting API limits.
        /// </summary>
        public static void ConfigureCloudAI(ITokenBucketRateLimiter limiter)
        {
            limiter.Configure("openai", 10, TimeSpan.FromMinutes(1));
            limiter.Configure("anthropic", 10, TimeSpan.FromMinutes(1));
            limiter.Configure("gemini", 15, TimeSpan.FromMinutes(1));
            limiter.Configure("groq", 20, TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// Configures rate limits for music metadata APIs.
        /// Follows their documented rate limits.
        /// </summary>
        public static void ConfigureMusicAPIs(ITokenBucketRateLimiter limiter)
        {
            // MusicBrainz requires 1 request per second
            limiter.Configure("musicbrainz", 1, TimeSpan.FromSeconds(1));
            // Last.fm allows ~5 requests per second
            limiter.Configure("lastfm", 5, TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Configures rate limits for streaming services.
        /// </summary>
        public static void ConfigureStreamingServices(ITokenBucketRateLimiter limiter)
        {
            limiter.Configure("tidal", 60, TimeSpan.FromMinutes(1));
            limiter.Configure("qobuz", 60, TimeSpan.FromMinutes(1));
            limiter.Configure("spotify", 30, TimeSpan.FromMinutes(1));
        }
    }
}
