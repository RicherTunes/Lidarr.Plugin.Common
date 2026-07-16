using System;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Lidarr.Plugin.Common.Extensions;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Services.Caching;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Registration;
using MyPlugin.Policies;

namespace MyPlugin;

public sealed class MyPluginModule : StreamingPluginModule
{
    public override string ServiceName => "MyPlugin";

    public override string Description => "Lidarr streaming plugin scaffold.";

    public override string Author => "RicherTunes";

    protected override bool HasIndexer() => false;

    protected override bool HasDownloadClient() => false;

    protected override bool SupportsAuthentication() => true;

    protected override bool SupportsCaching() => true;

    protected override void ConfigureServices(IServiceCollection services)
    {
        // Configure HttpClient and defaults
        services.AddHttpClient("myplugin")
            .ConfigureHttpClient((sp, c) =>
            {
                var settings = sp.GetRequiredService<MyPluginSettings>();
                c.BaseAddress = new Uri(settings.BaseUrl);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false
            });

        // Cache policy provider (used by StreamingResponseCache implementations)
        services.AddSingleton<ICachePolicyProvider, PolicyProvider>();

        // Example Resilience profile could be injected; using defaults for brevity

        // Bridge runtime defaults (auth-failure handler, indexer/download status reporters,
        // rate-limit reporter). TryAddSingleton semantics: call LAST so any custom
        // registrations above take precedence. Parity-checked by Common's
        // Check_RegistersBridgeDefaults - every StreamingPluginModule must call this.
        services.AddBridgeDefaults();
    }
}
