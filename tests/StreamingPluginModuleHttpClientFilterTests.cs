using System.Linq;
using System.Net.Http;
using Lidarr.Plugin.Abstractions.Capabilities;
using Lidarr.Plugin.Common.Services.Registration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public class StreamingPluginModuleHttpClientFilterTests
{
    [Fact]
    public void CreateServiceCollection_RemovesMetricsFactoryHttpMessageHandlerFilter()
    {
        // Regression guard: derived modules that call AddHttpClient must not leave the
        // M.E.Http MetricsFactoryHttpMessageHandlerFilter in the service collection. The
        // filter throws MissingMethodException for SocketsHttpHandler.get_MeterFactory at
        // runtime in isolated plugin AssemblyLoadContexts (see SuppressHttpClientMetricsFilter
        // doc-comment in StreamingPluginModule for the full backstory). The filter is
        // auto-registered by AddHttpClient; the suppression in CreateServiceCollection runs
        // after ConfigureServices and removes it.
        ServiceCollection services = new ModuleThatCallsAddHttpClient().CreateServiceCollection();

        bool filterPresent = services.Any(s =>
            s.ImplementationType?.FullName == "Microsoft.Extensions.Http.MetricsFactoryHttpMessageHandlerFilter");

        Assert.False(filterPresent,
            "MetricsFactoryHttpMessageHandlerFilter must be suppressed by StreamingPluginModule.CreateServiceCollection. " +
            "If this fails, SuppressHttpClientMetricsFilter was removed or stopped matching the filter type name.");
    }

    [Fact]
    public void CreateServiceCollection_PreservesIHttpClientFactoryRegistration()
    {
        // The suppression must NOT remove anything else. IHttpClientFactory and the
        // typed-client wiring registered by AddHttpClient must still be present so callers
        // can resolve their HTTP clients normally.
        ServiceCollection services = new ModuleThatCallsAddHttpClient().CreateServiceCollection();

        Assert.Contains(services, s => s.ServiceType == typeof(IHttpClientFactory));
    }

    private sealed class ModuleThatCallsAddHttpClient : StreamingPluginModule
    {
        public override string ServiceName => "Test";
        public override string Description => "Test module";
        public override string Author => "Tests";

        protected override void ConfigureServices(IServiceCollection services)
        {
            services.AddHttpClient("named");
        }
    }
}
