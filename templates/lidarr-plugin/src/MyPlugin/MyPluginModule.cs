using Lidarr.Plugin.Common.Services.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MyPlugin;

public static class MyPluginModule
{
    public static IServiceCollection AddMyPlugin(this IServiceCollection services, MyPluginSettings settings)
    {
        // Configure HttpClient and defaults
        services.AddHttpClient("myplugin")
            .ConfigureHttpClient(c => c.BaseAddress = new Uri(settings.BaseUrl));

        // Example Resilience profile could be injected; using defaults for brevity
        return services;
    }
}

