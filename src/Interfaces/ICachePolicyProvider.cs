using System.Collections.Generic;
using Lidarr.Plugin.Common.Services.Caching;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Provides cache policy selection for a given endpoint and parameter set.
    /// </summary>
public interface ICachePolicyProvider
    {
        CachePolicy GetPolicy(string endpoint, IReadOnlyDictionary<string, string> parameters);
    }
}
