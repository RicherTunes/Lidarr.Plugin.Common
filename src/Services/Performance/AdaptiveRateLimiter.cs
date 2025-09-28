using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Performance
{
    [Obsolete("Use IUniversalAdaptiveRateLimiter instead.", false)]
    public interface IAdaptiveRateLimiter
    {
        Task<bool> WaitIfNeededAsync(string endpoint, CancellationToken cancellationToken = default);
        void RecordResponse(string endpoint, HttpResponseMessage response);
        int GetCurrentLimit(string endpoint);
        RateLimitStats GetStats();
    }

    [Obsolete("Use UniversalAdaptiveRateLimiter instead.", false)]
    public class AdaptiveRateLimiter : IAdaptiveRateLimiter, IDisposable
    {
        private const string LegacyServiceName = "LegacyAdaptive";
        private readonly IUniversalAdaptiveRateLimiter _inner;

        public AdaptiveRateLimiter(Microsoft.Extensions.Logging.ILogger logger = null)
        {
            _inner = new UniversalAdaptiveRateLimiter();
        }

        public async Task<bool> WaitIfNeededAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            return await _inner.WaitIfNeededAsync(LegacyServiceName, endpoint, cancellationToken).ConfigureAwait(false);
        }

        public void RecordResponse(string endpoint, HttpResponseMessage response)
        {
            _inner.RecordResponse(LegacyServiceName, endpoint, response);
        }

        public int GetCurrentLimit(string endpoint)
        {
            return _inner.GetCurrentLimit(LegacyServiceName, endpoint);
        }

        public RateLimitStats GetStats()
        {
            var stats = _inner.GetServiceStats(LegacyServiceName);
            return new RateLimitStats
            {
                EndpointLimits = stats.EndpointStats.ToDictionary(k => k.Key, v => v.Value.CurrentLimit),
                TotalEndpoints = stats.EndpointStats.Count
            };
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }

    [Obsolete("Legacy type retained for compatibility. Use UniversalAdaptiveRateLimiter stats instead.", false)]
    public class EndpointRateLimit
    {
        public int RequestsPerMinute { get; set; }
        public DateTime LastRequestTime { get; set; }
        public int ConsecutiveSuccesses { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    [Obsolete("Legacy type retained for compatibility. Use UniversalAdaptiveRateLimiter configuration instead.", false)]
    public class RateLimitConfig
    {
        public int RequestsPerSecond { get; set; }
        public int BurstSize { get; set; }
        public TimeSpan CooldownAfterLimit { get; set; }
    }

    public class RateLimitStats
    {
        public Dictionary<string, int> EndpointLimits { get; set; } = new();
        public int TotalEndpoints { get; set; }
    }
}
