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
    /// Interface for adaptive rate limiting across multiple streaming services
    /// </summary>
    public interface IUniversalAdaptiveRateLimiter : IDisposable
    {
        /// <summary>
        /// Wait if needed before making a request to prevent rate limiting
        /// </summary>
        /// <param name="service">Service name (e.g., "Tidal", "Qobuz")</param>
        /// <param name="endpoint">Endpoint path (e.g., "search", "albums")</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if request can proceed</returns>
        Task<bool> WaitIfNeededAsync(string service, string endpoint, CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Record response to adjust future rate limiting
        /// </summary>
        /// <param name="service">Service name</param>
        /// <param name="endpoint">Endpoint path</param>
        /// <param name="response">HTTP response to analyze</param>
        void RecordResponse(string service, string endpoint, HttpResponseMessage response);
        
        /// <summary>
        /// Get current rate limit for a specific service/endpoint
        /// </summary>
        /// <param name="service">Service name</param>
        /// <param name="endpoint">Endpoint path</param>
        /// <returns>Current requests per minute limit</returns>
        int GetCurrentLimit(string service, string endpoint);
        
        /// <summary>
        /// Get statistics for a specific service
        /// </summary>
        /// <param name="service">Service name</param>
        /// <returns>Rate limit statistics</returns>
        ServiceRateLimitStats GetServiceStats(string service);
        
        /// <summary>
        /// Get global rate limit statistics
        /// </summary>
        /// <returns>Global statistics across all services</returns>
        GlobalRateLimitStats GetGlobalStats();
    }

    /// <summary>
    /// Universal adaptive rate limiter supporting multiple streaming services
    /// Learns optimal rate limits per service/endpoint and adapts based on response patterns
    /// </summary>
    /// <remarks>
    /// Based on Qobuzarr's battle-tested implementation with multi-service support:
    /// - Per-service, per-endpoint rate tracking
    /// - Success-based rate increases
    /// - Failure-based backoff with multiple strategies
    /// - Statistical reporting for monitoring
    /// - Thread-safe operation across concurrent requests
    /// 
    /// Streaming service rate limit patterns:
    /// - Tidal: ~300-400 req/min, OAuth endpoints more restrictive
    /// - Qobuz: ~500-600 req/min, search endpoints can handle higher rates
    /// - Spotify: ~100-200 req/min, very strict on certain endpoints
    /// </remarks>
    public class UniversalAdaptiveRateLimiter : IUniversalAdaptiveRateLimiter, IDisposable
    {
        private readonly ConcurrentDictionary<string, ServiceRateLimiter> _serviceLimiters;
        private readonly SemaphoreSlim _globalSemaphore;
        private readonly object _statsLock = new();
        private bool _disposed;

        // Default configuration per service type
        private static readonly Dictionary<string, ServiceConfig> DefaultServiceConfigs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Tidal"] = new ServiceConfig(300, 50, 400),
            ["Qobuz"] = new ServiceConfig(500, 100, 600), 
            ["Spotify"] = new ServiceConfig(150, 30, 200),
            ["AppleMusic"] = new ServiceConfig(200, 50, 300),
            ["Deezer"] = new ServiceConfig(250, 50, 350),
            ["Default"] = new ServiceConfig(200, 50, 400) // Conservative default
        };

        public UniversalAdaptiveRateLimiter()
        {
            _serviceLimiters = new ConcurrentDictionary<string, ServiceRateLimiter>(StringComparer.OrdinalIgnoreCase);
            _globalSemaphore = new SemaphoreSlim(1, 1);
        }

        public async Task<bool> WaitIfNeededAsync(string service, string endpoint, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UniversalAdaptiveRateLimiter));

            var serviceLimiter = GetOrCreateServiceLimiter(service);
            return await serviceLimiter.WaitIfNeededAsync(endpoint, cancellationToken);
        }

        public void RecordResponse(string service, string endpoint, HttpResponseMessage response)
        {
            if (_disposed)
                return;

            var serviceLimiter = GetOrCreateServiceLimiter(service);
            serviceLimiter.RecordResponse(endpoint, response);
        }

        public int GetCurrentLimit(string service, string endpoint)
        {
            if (_disposed)
                return 0;

            var serviceLimiter = _serviceLimiters.GetValueOrDefault(service);
            return serviceLimiter?.GetCurrentLimit(endpoint) ?? GetServiceConfig(service).DefaultRequestsPerMinute;
        }

        public ServiceRateLimitStats GetServiceStats(string service)
        {
            if (_disposed)
                return new ServiceRateLimitStats { ServiceName = service };

            var serviceLimiter = _serviceLimiters.GetValueOrDefault(service);
            return serviceLimiter?.GetStats() ?? new ServiceRateLimitStats { ServiceName = service };
        }

        public GlobalRateLimitStats GetGlobalStats()
        {
            if (_disposed)
                return new GlobalRateLimitStats();

            lock (_statsLock)
            {
                var globalStats = new GlobalRateLimitStats();
                
                foreach (var kvp in _serviceLimiters)
                {
                    var serviceStats = kvp.Value.GetStats();
                    globalStats.ServiceStats[kvp.Key] = serviceStats;
                    globalStats.TotalRequests += serviceStats.TotalRequests;
                    globalStats.TotalErrors += serviceStats.TotalErrors;
                    globalStats.TotalRateLimitHits += serviceStats.TotalRateLimitHits;
                }

                globalStats.GlobalSuccessRate = globalStats.TotalRequests > 0 
                    ? (double)(globalStats.TotalRequests - globalStats.TotalErrors) / globalStats.TotalRequests 
                    : 0;

                return globalStats;
            }
        }

        private ServiceRateLimiter GetOrCreateServiceLimiter(string service)
        {
            return _serviceLimiters.GetOrAdd(service, serviceName =>
            {
                var config = GetServiceConfig(serviceName);
                return new ServiceRateLimiter(serviceName, config);
            });
        }

        private static ServiceConfig GetServiceConfig(string service)
        {
            return DefaultServiceConfigs.GetValueOrDefault(service) ?? DefaultServiceConfigs["Default"];
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _globalSemaphore?.Dispose();
            
            foreach (var limiter in _serviceLimiters.Values)
            {
                limiter?.Dispose();
            }
            
            _serviceLimiters.Clear();
        }
    }

    internal class ServiceRateLimiter : IDisposable
    {
        private readonly string _serviceName;
        private readonly ServiceConfig _config;
        private readonly ConcurrentDictionary<string, EndpointRateLimit> _endpointLimits;
        private readonly SemaphoreSlim _semaphore;
        private readonly object _lock = new();
        private bool _disposed;

        public ServiceRateLimiter(string serviceName, ServiceConfig config)
        {
            _serviceName = serviceName;
            _config = config;
            _endpointLimits = new ConcurrentDictionary<string, EndpointRateLimit>();
            _semaphore = new SemaphoreSlim(1, 1);
        }

        public async Task<bool> WaitIfNeededAsync(string endpoint, CancellationToken cancellationToken)
        {
            if (_disposed)
                return false;

            var limit = _endpointLimits.GetOrAdd(endpoint, _ => new EndpointRateLimit
            {
                RequestsPerMinute = _config.DefaultRequestsPerMinute,
                LastRequest = DateTime.MinValue,
                ConsecutiveSuccesses = 0,
                ConsecutiveErrors = 0
            });

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var now = DateTime.UtcNow;
                var timeSinceLastRequest = now - limit.LastRequest;
                var minInterval = TimeSpan.FromMilliseconds(60000.0 / limit.RequestsPerMinute);

                if (timeSinceLastRequest < minInterval)
                {
                    var delay = minInterval - timeSinceLastRequest;
                    await Task.Delay(delay, cancellationToken);
                }

                limit.LastRequest = DateTime.UtcNow;
                limit.TotalRequests++;
                return true;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void RecordResponse(string endpoint, HttpResponseMessage response)
        {
            if (_disposed)
                return;

            var limit = _endpointLimits.GetOrAdd(endpoint, _ => new EndpointRateLimit
            {
                RequestsPerMinute = _config.DefaultRequestsPerMinute,
                LastRequest = DateTime.MinValue
            });

            lock (_lock)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    HandleRateLimitResponse(limit);
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized && limit.ConsecutiveErrors > 2)
                {
                    HandleSoftRateLimit(limit);
                }
                else if (!response.IsSuccessStatusCode)
                {
                    HandleErrorResponse(limit);
                }
                else
                {
                    HandleSuccessResponse(limit);
                }
            }
        }

        public int GetCurrentLimit(string endpoint)
        {
            return _endpointLimits.TryGetValue(endpoint, out var limit) 
                ? limit.RequestsPerMinute 
                : _config.DefaultRequestsPerMinute;
        }

        public ServiceRateLimitStats GetStats()
        {
            var stats = new ServiceRateLimitStats { ServiceName = _serviceName };
            
            foreach (var kvp in _endpointLimits)
            {
                var limit = kvp.Value;
                stats.EndpointStats[kvp.Key] = new EndpointStats
                {
                    CurrentLimit = limit.RequestsPerMinute,
                    TotalRequests = limit.TotalRequests,
                    SuccessfulRequests = limit.SuccessfulRequests,
                    TotalErrors = limit.TotalErrors,
                    RateLimitHits = limit.RateLimitHits,
                    SuccessRate = limit.TotalRequests > 0 
                        ? (double)limit.SuccessfulRequests / limit.TotalRequests 
                        : 0
                };
                
                stats.TotalRequests += limit.TotalRequests;
                stats.TotalErrors += limit.TotalErrors;
                stats.TotalRateLimitHits += limit.RateLimitHits;
            }

            return stats;
        }

        private void HandleRateLimitResponse(EndpointRateLimit limit)
        {
            limit.RateLimitHits++;
            limit.ConsecutiveErrors = 0;
            limit.ConsecutiveSuccesses = 0;

            var oldLimit = limit.RequestsPerMinute;
            limit.RequestsPerMinute = Math.Max(_config.MinRequestsPerMinute, 
                (int)(limit.RequestsPerMinute * 0.75));
        }

        private void HandleSoftRateLimit(EndpointRateLimit limit)
        {
            var oldLimit = limit.RequestsPerMinute;
            limit.RequestsPerMinute = Math.Max(_config.MinRequestsPerMinute,
                (int)(limit.RequestsPerMinute * 0.85));
            limit.ConsecutiveErrors = 0;
        }

        private void HandleErrorResponse(EndpointRateLimit limit)
        {
            limit.ConsecutiveErrors++;
            limit.ConsecutiveSuccesses = 0;
            limit.TotalErrors++;

            if (limit.ConsecutiveErrors >= 5)
            {
                var oldLimit = limit.RequestsPerMinute;
                limit.RequestsPerMinute = Math.Max(_config.MinRequestsPerMinute,
                    (int)(limit.RequestsPerMinute * 0.9));
            }
        }

        private void HandleSuccessResponse(EndpointRateLimit limit)
        {
            limit.ConsecutiveSuccesses++;
            limit.ConsecutiveErrors = 0;
            limit.SuccessfulRequests++;

            if (limit.ConsecutiveSuccesses >= 20 && 
                limit.RequestsPerMinute < _config.MaxRequestsPerMinute)
            {
                var oldLimit = limit.RequestsPerMinute;
                limit.RequestsPerMinute = Math.Min(_config.MaxRequestsPerMinute,
                    (int)(limit.RequestsPerMinute * 1.2));
                limit.ConsecutiveSuccesses = 0;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _semaphore?.Dispose();
        }

        private class EndpointRateLimit
        {
            public int RequestsPerMinute { get; set; }
            public DateTime LastRequest { get; set; }
            public int ConsecutiveSuccesses { get; set; }
            public int ConsecutiveErrors { get; set; }
            public long TotalRequests { get; set; }
            public long SuccessfulRequests { get; set; }
            public long TotalErrors { get; set; }
            public long RateLimitHits { get; set; }
        }
    }

    internal class ServiceConfig
    {
        public int DefaultRequestsPerMinute { get; }
        public int MinRequestsPerMinute { get; }
        public int MaxRequestsPerMinute { get; }

        public ServiceConfig(int defaultReqPerMin, int minReqPerMin, int maxReqPerMin)
        {
            DefaultRequestsPerMinute = defaultReqPerMin;
            MinRequestsPerMinute = minReqPerMin;
            MaxRequestsPerMinute = maxReqPerMin;
        }
    }

    public class ServiceRateLimitStats
    {
        public string ServiceName { get; set; } = string.Empty;
        public Dictionary<string, EndpointStats> EndpointStats { get; set; } = new();
        public long TotalRequests { get; set; }
        public long TotalErrors { get; set; }
        public long TotalRateLimitHits { get; set; }
        public double ServiceSuccessRate => TotalRequests > 0 ? (double)(TotalRequests - TotalErrors) / TotalRequests : 0;
    }

    public class GlobalRateLimitStats
    {
        public Dictionary<string, ServiceRateLimitStats> ServiceStats { get; set; } = new();
        public long TotalRequests { get; set; }
        public long TotalErrors { get; set; }
        public long TotalRateLimitHits { get; set; }
        public double GlobalSuccessRate { get; set; }
    }

    public class EndpointStats
    {
        public int CurrentLimit { get; set; }
        public long TotalRequests { get; set; }
        public long SuccessfulRequests { get; set; }
        public long TotalErrors { get; set; }
        public long RateLimitHits { get; set; }
        public double SuccessRate { get; set; }
    }
}