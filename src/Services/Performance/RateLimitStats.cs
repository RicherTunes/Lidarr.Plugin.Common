using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Performance
{
    /// <summary>
    /// Snapshot of rate-limit state for a service or endpoint. Returned by adaptive
    /// rate-limiter consumers (e.g. Qobuzarr's AdaptiveQobuzApiClient) to expose the
    /// currently-effective limit map without leaking the limiter's internal state.
    /// </summary>
    public class RateLimitStats
    {
        public Dictionary<string, int> EndpointLimits { get; set; } = new();
        public int TotalEndpoints { get; set; }
    }
}
