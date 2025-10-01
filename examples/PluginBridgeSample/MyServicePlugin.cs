using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace PluginBridgeSample
{
    // snippet:bridge-plugin
    // snippet-skip-compile
    public sealed class MyServicePlugin : StreamingPlugin<MyServiceModule, MyServiceSettings>
    {
        public MyServicePlugin() : base("MyService")
        {
        }
    }
    // end-snippet

    // snippet:bridge-module
    // snippet-skip-compile
    public sealed class MyServiceModule : IStreamingPluginModule<MyServiceSettings>, IAsyncStartable
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddHttpClient("myservice");
            services.AddSingleton<MyServiceClient>();
        }

        public Task StartAsync(IServiceProvider services, CancellationToken cancellationToken)
            => services.GetRequiredService<MyServiceClient>().WarmUpAsync(cancellationToken);
    }
    // end-snippet

    // snippet:bridge-settings
    // snippet-skip-compile
    public sealed class MyServiceSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string Region { get; set; } = "US";
    }
    // end-snippet

    internal sealed class MyServiceClient
    {
        public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Stub interfaces from Lidarr.Plugin.Common.Hosting to keep the sample self-contained for snippet compilation
    public abstract class StreamingPlugin<TModule, TSettings>
    {
        protected StreamingPlugin(string serviceName) => ServiceName = serviceName;
        protected string ServiceName { get; }
    }

    public interface IStreamingPluginModule<TSettings>
    {
        void ConfigureServices(IServiceCollection services);
    }

    public interface IAsyncStartable
    {
        Task StartAsync(IServiceProvider services, CancellationToken cancellationToken);
    }
}
