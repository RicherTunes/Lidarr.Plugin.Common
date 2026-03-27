using System;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Extensions;
using Lidarr.Plugin.Common.Services.Bridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

public class BridgeDefaultsActivationTests
{
    [Fact]
    public void AddBridgeDefaults_Resolves_All_Four_Reporters()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddBridgeDefaults();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IAuthFailureHandler>());
        Assert.NotNull(provider.GetService<IIndexerStatusReporter>());
        Assert.NotNull(provider.GetService<IDownloadStatusReporter>());
        Assert.NotNull(provider.GetService<IRateLimitReporter>());
    }

    [Fact]
    public void AddBridgeDefaults_Resolves_Correct_Implementation_Types()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddBridgeDefaults();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<DefaultAuthFailureHandler>(provider.GetRequiredService<IAuthFailureHandler>());
        Assert.IsType<DefaultIndexerStatusReporter>(provider.GetRequiredService<IIndexerStatusReporter>());
        Assert.IsType<DefaultDownloadStatusReporter>(provider.GetRequiredService<IDownloadStatusReporter>());
        Assert.IsType<DefaultRateLimitReporter>(provider.GetRequiredService<IRateLimitReporter>());
    }

    [Fact]
    public void AddBridgeDefaults_Does_Not_Override_Custom_Registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Register custom BEFORE defaults
        services.AddSingleton<IAuthFailureHandler>(new StubAuthHandler());
        services.AddBridgeDefaults();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<StubAuthHandler>(provider.GetRequiredService<IAuthFailureHandler>());
        // Other defaults still resolve
        Assert.IsType<DefaultIndexerStatusReporter>(provider.GetRequiredService<IIndexerStatusReporter>());
    }

    [Fact]
    public void AddBridgeDefaults_Without_Logger_Throws_On_Resolve()
    {
        var services = new ServiceCollection();
        // No ILoggerFactory registered
        services.AddBridgeDefaults();

        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IAuthFailureHandler>());
    }

    private sealed class StubAuthHandler : IAuthFailureHandler
    {
        public AuthStatus Status => AuthStatus.Unknown;
        public System.Threading.Tasks.ValueTask HandleFailureAsync(AuthFailure f, System.Threading.CancellationToken ct = default) => default;
        public System.Threading.Tasks.ValueTask HandleSuccessAsync(System.Threading.CancellationToken ct = default) => default;
        public System.Threading.Tasks.ValueTask RequestReauthenticationAsync(string r, System.Threading.CancellationToken ct = default) => default;
    }
}
