using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// Simple defensive wrapper that prevents service failures from breaking the entire optimization chain
    /// Provides graceful degradation and fallback values when services fail
    /// </summary>
    public class DefensiveServiceWrapper<T> where T : class
    {
        private readonly T _service;
        private readonly ILogger<DefensiveServiceWrapper<T>> _logger;
        private readonly string _serviceName;
        private volatile bool _isHealthy = true;
        private volatile int _consecutiveFailures = 0;
        private readonly int _maxFailuresBeforeCircuitBreak = 5;

        public DefensiveServiceWrapper(T service, ILogger<DefensiveServiceWrapper<T>> logger)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceName = typeof(T).Name;
        }

        /// <summary>
        /// Executes service operation safely with fallback value
        /// </summary>
        public TResult ExecuteSafely<TResult>(
            Func<T, TResult> operation, 
            TResult fallbackValue = default,
            string operationName = "Operation")
        {
            if (!_isHealthy)
            {
                _logger.LogDebug("🛡️ CIRCUIT BREAKER: {0}.{1} blocked - service unhealthy", _serviceName, operationName);
                return fallbackValue;
            }

            try
            {
                var result = operation(_service);
                
                // Reset failure count on success
                if (_consecutiveFailures > 0)
                {
                    _consecutiveFailures = 0;
                    _logger.LogDebug("✅ SERVICE RECOVERED: {0} back to healthy state", _serviceName);
                }
                
                return result;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                
                _logger.LogWarning(ex, "⚠️ SERVICE FAILURE: {0}.{1} failed (attempt {2}), using fallback", 
                           _serviceName, operationName, _consecutiveFailures);

                // Circuit breaker - mark unhealthy after too many failures
                if (_consecutiveFailures >= _maxFailuresBeforeCircuitBreak)
                {
                    _isHealthy = false;
                    _logger.LogError("🚫 CIRCUIT BREAKER OPENED: {0} marked unhealthy after {1} failures", 
                                _serviceName, _consecutiveFailures);
                }

                return fallbackValue;
            }
        }

        /// <summary>
        /// Executes service operation safely without return value
        /// </summary>
        public void ExecuteSafely(Action<T> operation, string operationName = "Operation")
        {
            ExecuteSafely<object>(service => { operation(service); return null; }, null, operationName);
        }

        /// <summary>
        /// Manually reset service health (for testing or recovery)
        /// </summary>
        public void ResetHealth()
        {
            _isHealthy = true;
            _consecutiveFailures = 0;
            _logger.LogInformation("🔄 SERVICE RESET: {0} health manually reset", _serviceName);
        }

        /// <summary>
        /// Get current service health status
        /// </summary>
        public bool IsHealthy => _isHealthy;
        public int ConsecutiveFailures => _consecutiveFailures;
    }

    /// <summary>
    /// Static helper for creating defensive wrappers
    /// </summary>
    public static class DefensiveService
    {
        public static DefensiveServiceWrapper<T> Wrap<T>(T service, ILogger<DefensiveServiceWrapper<T>> logger) where T : class
        {
            return new DefensiveServiceWrapper<T>(service, logger);
        }
    }
}