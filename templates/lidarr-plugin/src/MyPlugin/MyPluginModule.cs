using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Services.Caching;
using Lidarr.Plugin.Common.Interfaces;
using MyPlugin.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace MyPlugin;

public static class MyPluginModule
{
    public static IServiceCollection AddMyPlugin(this IServiceCollection services, MyPluginSettings settings)
    {
        // Configure HttpClient and defaults
        services.AddHttpClient("myplugin")
            .ConfigureHttpClient(c => c.BaseAddress = new Uri(settings.BaseUrl));

        // Cache policy provider (used by StreamingResponseCache implementations)
        services.AddSingleton<ICachePolicyProvider, PolicyProvider>();

        // Example Resilience profile could be injected; using defaults for brevity
        return services;
    }
}
