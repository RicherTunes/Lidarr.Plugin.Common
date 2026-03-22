using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lidarr.Plugin.Common.Extensions;

/// <summary>
/// DI registration for default bridge infrastructure.
/// Call AddBridgeDefaults() LAST in ConfigureServices so custom registrations take precedence.
/// </summary>
public static class BridgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers default bridge implementations for auth, indexer status, and rate limit reporting.
    /// Uses TryAddSingleton so plugins that register custom implementations first take precedence.
    /// </summary>
    public static IServiceCollection AddBridgeDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton<IAuthFailureHandler, DefaultAuthFailureHandler>();
        services.TryAddSingleton<IIndexerStatusReporter, DefaultIndexerStatusReporter>();
        services.TryAddSingleton<IRateLimitReporter, DefaultRateLimitReporter>();
        return services;
    }
}
