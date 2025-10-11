using System.Collections.Generic;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Caching;

namespace MyPlugin.Policies;

/// <summary>
/// Minimal cache policy provider. Adjust per endpoint as needed.
/// </summary>
public sealed class PolicyProvider : ICachePolicyProvider
{
    public CachePolicy GetPolicy(string endpoint, IReadOnlyDictionary<string, string> parameters)
    {
        endpoint = (endpoint ?? string.Empty).ToLowerInvariant();

        if (endpoint.Contains("/v1/search"))
        {
            // Short cache for search to keep results fresh
            return CachePolicy.Default.With(
                name: "search",
                duration: System.TimeSpan.FromMinutes(2),
                slidingExpiration: System.TimeSpan.FromMinutes(1),
                varyByScope: false,
                enableConditionalRevalidation: false);
        }

        if (endpoint.Contains("/v1/details"))
        {
            // Details are stable; allow ETag revalidation and longer TTL
            return CachePolicy.Default.With(
                name: "details",
                duration: System.TimeSpan.FromMinutes(10),
                slidingExpiration: System.TimeSpan.FromMinutes(5),
                varyByScope: false,
                enableConditionalRevalidation: true,
                slidingRefreshWindow: System.TimeSpan.FromSeconds(30));
        }

        // Fallback
        return CachePolicy.Default.With(duration: System.TimeSpan.FromMinutes(5));
    }
}

