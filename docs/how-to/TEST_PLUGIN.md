# How-to: Test a Plugin with the Isolation Loader

If you prefer to reuse the shared fixtures and HTTP simulators, see [Leverage the Common TestKit](TEST_WITH_TESTKIT.md).

A plugin test suite should load the plugin the same way the host does: through `PluginLoader` and a dedicated `AssemblyLoadContext`. Use this guide when you see `No test is available` or when you need confidence that side-by-side `Lidarr.Plugin.Common` versions work.

## 1. Add a test project next to the plugin

- Create a test project (xUnit or NUnit). Example `csproj`:

  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net8.0</TargetFramework>
      <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
      <PackageReference Include="xunit" Version="2.6.5" />
      <PackageReference Include="xunit.runner.visualstudio" Version="2.5.7" />
      <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.2" />
    </ItemGroup>

    <ItemGroup>
      <ProjectReference Include="..\src\MyPlugin\MyPlugin.csproj" />
      <ProjectReference Include="..\src\Lidarr.Plugin.Common\Lidarr.Plugin.Common.csproj" />
      <ProjectReference Include="..\src\Lidarr.Plugin.Abstractions\Lidarr.Plugin.Abstractions.csproj" />
    </ItemGroup>
  </Project>
  ```

- Keep the plugin and test project targeting the same TFM (`net8.0`).
- Reference the plugin project so your tests can access internal types (add `InternalsVisibleTo` if needed).

## 2. Load the plugin through `PluginLoader`

Create a fixture that publishes the plugin into a temporary folder, then invokes `PluginLoader.LoadAsync`. This mimics the host and ensures a private `AssemblyLoadContext` per test run.

```csharp
public sealed class PluginFixture : IAsyncLifetime
{
    private PluginHandle? _handle;
    private TestPluginBuilder? _builder;
    public IPlugin? Plugin => _handle?.Plugin;

    public async Task InitializeAsync()
    {
        _builder = new TestPluginBuilder();
        var pluginDir = _builder.BuildPlugin("TidalarrSmoke", "1.1.4");

        var request = new PluginLoadRequest
        {
            PluginDirectory = pluginDir,
            HostVersion = new Version(2, 14, 0, 0),
            ContractVersion = typeof(IPlugin).Assembly.GetName().Version!,
            PluginContext = new DefaultPluginContext(new Version(2, 14, 0, 0), NullLoggerFactory.Instance)
        };

        _handle = await PluginLoader.LoadAsync(request);
    }

    public async Task DisposeAsync()
    {
        if (_handle != null)
        {
            await _handle.DisposeAsync();
        }

        _builder?.Dispose();
    }
}

public class SmokeTests : IClassFixture<PluginFixture>
{
    private readonly PluginFixture _fixture;
    public SmokeTests(PluginFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Indexer_returns_results()
    {
        await using var indexer = await _fixture.Plugin!.CreateIndexerAsync();
        var tracks = await indexer!.SearchTracksAsync("tidalarr proof");
        Assert.NotEmpty(tracks);
    }
}
```

> The example uses `TestPluginBuilder` from `tests/Isolation/TestPluginBuilder.cs` to generate a packaged plugin on the fly. Swap it for `dotnet publish` if you prefer to exercise a real build artifact.

## 3. Validate manifest and settings in tests

- Call `PluginHandle.Plugin.Manifest` to assert `ApiVersion`, `MinHostVersion`, and `CommonVersion`.
- Inject `ISettingsProvider` via the host context stub and use the bridges in `Lidarr.Plugin.Common.Hosting.Settings` to hydrate strongly typed settings.
- Add negative tests that ensure incompatible manifests throw (`tests/PluginIsolationTests.cs` shows examples).

## 4. Run tests locally and in CI

- Local: `dotnet test tests/MyPlugin.Tests/MyPlugin.Tests.csproj -f net8.0`.
- CI: add a job that builds the plugin, runs `dotnet test`, and publishes the artifacts produced by `TestPluginBuilder` for troubleshooting.
- To avoid `No test is available`, confirm your test project references a supported test SDK and contains concrete `[Fact]`/`[Test]` methods.

## 5. Reuse the sample harness

- `tests/PluginIsolationTests.cs` demonstrates side-by-side Common versions and unload verification.
- `tests/Isolation/TestPluginBuilder.cs` and `tests/Isolation/Templates/PluginTemplate.cs.txt` show how to generate throwaway plugins during tests.
- `docs/examples/ISOLATION_HOST_SAMPLE.md` walks through a console host that reuses the same loading primitives.

### Checklist

- [ ] Tests reference Abstractions and Common.
- [ ] Fixture enters a collectible `PluginLoadContext` and disposes it (double `GC.Collect` if you assert unload).
- [ ] Manifests validated (happy path + failure cases).
- [ ] Settings provider bridge exercised if your plugin consumes settings.
- [ ] CI job publishes test artifacts when failures occur.

Need finer control? Extend `TestPluginBuilder` or copy the sample harness into your plugin repo and customize the generated manifest.
