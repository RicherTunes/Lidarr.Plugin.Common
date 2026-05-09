using System.Linq;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Extensions;
using Lidarr.Plugin.Common.Services.Bridge;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Extensions;

public class BridgeServiceCollectionExtensionsTests
{
    [Fact]
    public void AddBridgeDefaults_RegistersAllFourBridgeContracts()
    {
        var services = new ServiceCollection();
        services.AddLogging();                           // default impls take ILogger<T>
        services.AddBridgeDefaults();
        var sp = services.BuildServiceProvider();

        Assert.IsType<DefaultAuthFailureHandler>(sp.GetRequiredService<IAuthFailureHandler>());
        Assert.IsType<DefaultIndexerStatusReporter>(sp.GetRequiredService<IIndexerStatusReporter>());
        Assert.IsType<DefaultDownloadStatusReporter>(sp.GetRequiredService<IDownloadStatusReporter>());
        Assert.IsType<DefaultRateLimitReporter>(sp.GetRequiredService<IRateLimitReporter>());
    }

    [Fact]
    public void AddBridgeDefaults_ReturnsSameCollection_ForFluentChaining()
    {
        var services = new ServiceCollection();
        var returned = services.AddBridgeDefaults();
        Assert.Same(services, returned);
    }

    [Fact]
    public void AddBridgeDefaults_DoesNotOverridePreRegisteredAuthHandler()
    {
        // Plugins should be able to register custom impls FIRST and have them survive AddBridgeDefaults().
        var custom = new Mock<IAuthFailureHandler>().Object;
        var services = new ServiceCollection();
        services.AddSingleton(custom);

        services.AddBridgeDefaults();

        var sp = services.BuildServiceProvider();
        Assert.Same(custom, sp.GetRequiredService<IAuthFailureHandler>());
    }

    [Fact]
    public void AddBridgeDefaults_DoesNotOverridePreRegisteredIndexerReporter()
    {
        var custom = new Mock<IIndexerStatusReporter>().Object;
        var services = new ServiceCollection();
        services.AddSingleton(custom);

        services.AddBridgeDefaults();

        var sp = services.BuildServiceProvider();
        Assert.Same(custom, sp.GetRequiredService<IIndexerStatusReporter>());
    }

    [Fact]
    public void AddBridgeDefaults_CalledTwice_DoesNotDuplicateRegistrations()
    {
        var services = new ServiceCollection();
        services.AddBridgeDefaults();
        services.AddBridgeDefaults();

        // TryAdd semantics + only 4 distinct contracts → exactly 4 bridge registrations.
        var bridgeRegs = services.Count(d =>
            d.ServiceType == typeof(IAuthFailureHandler) ||
            d.ServiceType == typeof(IIndexerStatusReporter) ||
            d.ServiceType == typeof(IDownloadStatusReporter) ||
            d.ServiceType == typeof(IRateLimitReporter));
        Assert.Equal(4, bridgeRegs);
    }

    [Fact]
    public void AddBridgeDefaults_ResolvedAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBridgeDefaults();
        var sp = services.BuildServiceProvider();

        var first = sp.GetRequiredService<IAuthFailureHandler>();
        var second = sp.GetRequiredService<IAuthFailureHandler>();
        Assert.Same(first, second);
    }

}
