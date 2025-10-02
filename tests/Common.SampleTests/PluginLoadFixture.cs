using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.SampleTests;

// snippet:streaming-plugin-fixture
public sealed class PluginLoadFixture : IAsyncLifetime
{
    private AssemblyLoadContext? _loadContext;

    public IPlugin Plugin { get; private set; } = default!;
    public IServiceProvider Services { get; private set; } = default!;
    public IPluginContext PluginContext { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        var buildOutput = Path.GetFullPath(Path.Combine("plugins", "MyPlugin", "bin", "Debug", "net8.0"));
        if (!Directory.Exists(buildOutput))
        {
            throw new DirectoryNotFoundException($"Plugin build output not found at '{buildOutput}'. Run `dotnet publish` first.");
        }

        var pluginAssemblyPath = Directory.GetFiles(buildOutput, "MyPlugin.dll", SearchOption.TopDirectoryOnly)
                                          .Single();

        _loadContext = new AssemblyLoadContext("MyPlugin.TestContext", isCollectible: true);
        using (_loadContext.EnterContextualReflection())
        {
            var pluginAssembly = _loadContext.LoadFromAssemblyPath(pluginAssemblyPath);
            var pluginType = pluginAssembly.DefinedTypes.First(type => typeof(IPlugin).IsAssignableFrom(type) && !type.IsAbstract);

            Plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
            PluginContext = new FakePluginContext();
            await Plugin.InitializeAsync(PluginContext, CancellationToken.None).ConfigureAwait(false);
        }

        Services = ResolveServices(Plugin);
    }

    public async Task DisposeAsync()
    {
        if (Plugin is not null)
        {
            await Plugin.DisposeAsync().ConfigureAwait(false);
        }

        _loadContext?.Unload();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static IServiceProvider ResolveServices(IPlugin plugin)
    {
        if (plugin is IServiceProviderAccessor accessor)
        {
            return accessor.Services;
        }

        var servicesProperty = plugin.GetType().GetProperty("Services", BindingFlags.Instance | BindingFlags.NonPublic);
        if (servicesProperty?.GetValue(plugin) is IServiceProvider services)
        {
            return services;
        }

        throw new InvalidOperationException("Plugin must expose IServiceProvider (implement IServiceProviderAccessor to avoid reflection).");
    }
}
// end-snippet

public sealed class FakePluginContext : IPluginContext
{
    private readonly ILoggerFactory _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));

    public Version HostVersion { get; } = new(2, 0, 0);
    public ILoggerFactory LoggerFactory => _loggerFactory;
    public IServiceProvider? Services { get; } = new ServiceCollection()
        .AddLogging()
        .BuildServiceProvider();
}

public interface IServiceProviderAccessor
{
    IServiceProvider Services { get; }
}

// snippet:streaming-plugin-smoke-test
public sealed class PluginSmokeTests : IClassFixture<PluginLoadFixture>
{
    private readonly PluginLoadFixture _fixture;

    public PluginSmokeTests(PluginLoadFixture fixture) => _fixture = fixture;

    [Fact(Skip = "Sample template; remove Skip once the plugin publishes binaries for tests.")]
    public void PluginLoads()
    {
        Assert.NotNull(_fixture.Plugin);
        Assert.NotNull(_fixture.Services);
    }
}
// end-snippet


