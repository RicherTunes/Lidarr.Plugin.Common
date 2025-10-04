using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Provides a resilience policy for a given named profile (e.g., "search", "details", "catalog", "download").
    /// Consumers decide how policies are implemented (Polly, custom, or built-in).
    /// </summary>
    public interface IResiliencePolicyProvider
    {
        ResiliencePolicy Get(string profileName);
    }
}

