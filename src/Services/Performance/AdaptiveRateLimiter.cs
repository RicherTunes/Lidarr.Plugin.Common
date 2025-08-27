using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Performance
{
    /// <summary>
    /// Adaptive rate limiting service that prevents API bans across all streaming plugins
    /// Learns optimal rates per endpoint and coordinates across multiple plugins
    /// UNIVERSAL: All streaming APIs have rate limits that need intelligent management
    /// </summary>
    public interface IAdaptiveRateLimiter
    {
        Task<bool> WaitIfNeededAsync(string endpoint, CancellationToken cancellationToken = default);
        void RecordResponse(string endpoint, HttpResponseMessage response);
        int GetCurrentLimit(string endpoint);
        RateLimitStats GetStats();
    }

    public class AdaptiveRateLimiter : IAdaptiveRateLimiter
    {
        private readonly ConcurrentDictionary<string, EndpointRateLimit> _endpointLimits;
        private readonly SemaphoreSlim _globalSemaphore;
        private DateTime _lastGlobalRequest = DateTime.MinValue;
        private readonly object _lock = new();

        // Configuration
        private const int DEFAULT_REQUESTS_PER_MINUTE = 60;
        private const int MIN_REQUESTS_PER_MINUTE = 10;
        private const int MAX_REQUESTS_PER_MINUTE = 500;
        private const double RATE_REDUCTION_FACTOR = 0.75;
        private const double RATE_INCREASE_FACTOR = 1.2;
        private const int SUCCESS_THRESHOLD_FOR_INCREASE = 20;

        public AdaptiveRateLimiter()
        {
            _endpointLimits = new ConcurrentDictionary<string, EndpointRateLimit>();
            _globalSemaphore = new SemaphoreSlim(1, 1);
        }

        public async Task<bool> WaitIfNeededAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            var limit = _endpointLimits.GetOrAdd(endpoint, _ => new EndpointRateLimit
            {
                RequestsPerMinute = DEFAULT_REQUESTS_PER_MINUTE,
                LastRequestTime = DateTime.MinValue,
                ConsecutiveSuccesses = 0,
                ConsecutiveFailures = 0
            });

            await _globalSemaphore.WaitAsync(cancellationToken);
            try
            {
                var now = DateTime.UtcNow;
                var timeSinceLastRequest = now - limit.LastRequestTime;
                var minimumInterval = TimeSpan.FromMinutes(1.0 / limit.RequestsPerMinute);

                if (timeSinceLastRequest < minimumInterval)
                {
                    var waitTime = minimumInterval - timeSinceLastRequest;
                    await Task.Delay(waitTime, cancellationToken);
                }

                limit.LastRequestTime = DateTime.UtcNow;
                return true;
            }
            finally
            {
                _globalSemaphore.Release();
            }
        }

        public void RecordResponse(string endpoint, HttpResponseMessage response)
        {
            if (!_endpointLimits.TryGetValue(endpoint, out var limit))
                return;

            lock (_lock)
            {
                if (IsRateLimited(response))
                {
                    limit.ConsecutiveFailures++;
                    limit.ConsecutiveSuccesses = 0;
                    
                    // Aggressive reduction on rate limit
                    var newRate = (int)(limit.RequestsPerMinute * RATE_REDUCTION_FACTOR);
                    limit.RequestsPerMinute = Math.Max(newRate, MIN_REQUESTS_PER_MINUTE);
                }
                else if (response.IsSuccessStatusCode)
                {
                    limit.ConsecutiveSuccesses++;
                    limit.ConsecutiveFailures = 0;
                    
                    // Gradual increase after sustained success
                    if (limit.ConsecutiveSuccesses >= SUCCESS_THRESHOLD_FOR_INCREASE)
                    {
                        var newRate = (int)(limit.RequestsPerMinute * RATE_INCREASE_FACTOR);
                        limit.RequestsPerMinute = Math.Min(newRate, MAX_REQUESTS_PER_MINUTE);
                        limit.ConsecutiveSuccesses = 0; // Reset counter
                    }
                }
            }
        }

        public int GetCurrentLimit(string endpoint)
        {
            return _endpointLimits.TryGetValue(endpoint, out var limit) 
                ? limit.RequestsPerMinute 
                : DEFAULT_REQUESTS_PER_MINUTE;
        }

        public RateLimitStats GetStats()
        {
            var stats = new Dictionary<string, int>();
            foreach (var kvp in _endpointLimits)
            {
                stats[kvp.Key] = kvp.Value.RequestsPerMinute;
            }
            
            return new RateLimitStats
            {
                EndpointLimits = stats,
                TotalEndpoints = stats.Count
            };
        }

        private static bool IsRateLimited(HttpResponseMessage response)
        {
            return response.StatusCode == HttpStatusCode.TooManyRequests ||
                   response.StatusCode == (HttpStatusCode)429 ||
                   (response.Headers.RetryAfter != null);
        }
    }

    public class EndpointRateLimit
    {
        public int RequestsPerMinute { get; set; }
        public DateTime LastRequestTime { get; set; }
        public int ConsecutiveSuccesses { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    public class RateLimitStats
    {
        public Dictionary<string, int> EndpointLimits { get; set; } = new();
        public int TotalEndpoints { get; set; }
    }
}