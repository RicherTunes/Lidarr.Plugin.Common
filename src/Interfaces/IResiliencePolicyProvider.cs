using System;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Provides a resilience policy for a given named profile (e.g., "search", "details", "catalog", "download").
    /// Consumers decide how policies are implemented (Polly, custom, or built-in).
    /// </summary>
    internal interface IResiliencePolicyProvider
    {
        ResilienceProfileSettings Get(string profileName);
    }
}

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>Lightweight, engine-agnostic resilience settings.</summary>
    public sealed class ResilienceProfileSettings
    {
        public string Name { get; init; } = "default";
        public int MaxRetries { get; init; } = 5;
        public TimeSpan? RetryBudget { get; init; } = TimeSpan.FromSeconds(60);
        public int MaxConcurrencyPerHost { get; init; } = 6;
        /// <summary>
        /// Aggregate cap across all profiles for a given host. When 0 or negative, falls back to <see cref="MaxConcurrencyPerHost"/>.
        /// </summary>
        public int MaxTotalConcurrencyPerHost { get; init; } = 0;
        public TimeSpan? PerRequestTimeout { get; init; }
    }
}
