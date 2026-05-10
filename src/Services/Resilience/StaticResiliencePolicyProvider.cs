using System;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// Default resilience-profile provider with sensible per-profile values for
    /// streaming-service plugins. Used directly, or wrapped by
    /// <see cref="FileResiliencePolicyProvider"/> as the fallback when the
    /// configuration file is missing/invalid.
    /// </summary>
    public sealed class StaticResiliencePolicyProvider : IResilienceSettingsProvider
    {
        /// <inheritdoc/>
        public ResilienceProfileSettings Get(string profileName)
        {
            var name = (profileName ?? "default").ToLowerInvariant();
            return name switch
            {
                "auth" => new ResilienceProfileSettings
                {
                    Name = "auth",
                    MaxRetries = 2,
                    RetryBudget = TimeSpan.FromSeconds(20),
                    MaxConcurrencyPerHost = 2,
                    PerRequestTimeout = TimeSpan.FromSeconds(10)
                },
                "search" => new ResilienceProfileSettings
                {
                    Name = "search",
                    MaxRetries = 6,
                    RetryBudget = TimeSpan.FromSeconds(60),
                    MaxConcurrencyPerHost = 12,
                    PerRequestTimeout = TimeSpan.FromSeconds(15)
                },
                "details" => new ResilienceProfileSettings
                {
                    Name = "details",
                    MaxRetries = 4,
                    RetryBudget = TimeSpan.FromSeconds(45),
                    MaxConcurrencyPerHost = 8,
                    PerRequestTimeout = TimeSpan.FromSeconds(20)
                },
                "catalog" => new ResilienceProfileSettings
                {
                    Name = "catalog",
                    MaxRetries = 5,
                    RetryBudget = TimeSpan.FromSeconds(60),
                    MaxConcurrencyPerHost = 8,
                    PerRequestTimeout = null
                },
                "library" => new ResilienceProfileSettings
                {
                    Name = "library",
                    MaxRetries = 5,
                    RetryBudget = TimeSpan.FromSeconds(60),
                    MaxConcurrencyPerHost = 6,
                    PerRequestTimeout = null
                },
                "download" => new ResilienceProfileSettings
                {
                    Name = "download",
                    MaxRetries = 3,
                    RetryBudget = TimeSpan.FromSeconds(90),
                    MaxConcurrencyPerHost = 3,
                    PerRequestTimeout = TimeSpan.FromMinutes(2)
                },
                _ => new ResilienceProfileSettings
                {
                    Name = "default",
                    MaxRetries = 5,
                    RetryBudget = TimeSpan.FromSeconds(60),
                    MaxConcurrencyPerHost = 6,
                    PerRequestTimeout = null
                }
            };
        }
    }
}
