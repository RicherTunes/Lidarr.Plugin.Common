using System;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Extensions;
using Lidarr.Plugin.Common.TestKit.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.TestKit.Fixtures;

/// <summary>
/// Fixture that builds a real DI container with default bridge implementations.
/// Use this for fixture-backed compliance testing instead of mocks.
/// </summary>
public sealed class BridgeComplianceFixture : IDisposable
{
    public PluginTestContext Context { get; }
    public IServiceProvider Services { get; }

    public IAuthFailureHandler AuthHandler => Services.GetRequiredService<IAuthFailureHandler>();
    public IIndexerStatusReporter StatusReporter => Services.GetRequiredService<IIndexerStatusReporter>();
    public IDownloadStatusReporter DownloadStatusReporter => Services.GetRequiredService<IDownloadStatusReporter>();
    public IRateLimitReporter RateLimitReporter => Services.GetRequiredService<IRateLimitReporter>();

    public BridgeComplianceFixture()
    {
        Context = new PluginTestContext(new Version(3, 1, 0, 0));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Context.LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddBridgeDefaults();

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public void Dispose()
    {
        (Services as IDisposable)?.Dispose();
        Context.Dispose();
    }
}
