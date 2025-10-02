# How-to: Test `StreamingPlugin<TModule, TSettings>`

This guide shows the minimal steps to load your plugin in a collectible `AssemblyLoadContext`, resolve services, and run smoke tests without fighting the infrastructure.

## 1. Expose services from your plugin

Add an accessor so tests can reach the DI container. The sample plugin inherits from `StreamingPlugin<TModule, TSettings>` and implements a simple indexer.

```csharp file=../../examples/StreamingPluginSample/SampleStreamingPlugin.cs#streaming-plugin-entry
```

```csharp file=../../examples/StreamingPluginSample/SampleStreamingPlugin.cs#streaming-plugin-module
```

```csharp file=../../examples/StreamingPluginSample/SampleStreamingPlugin.cs#streaming-plugin-settings
```

```csharp file=../../examples/StreamingPluginSample/SampleStreamingPlugin.cs#streaming-plugin-indexer
```

> Tip: if your plugin already returns `null` for an unsupported feature (e.g., no download client), keep doing so. The host will handle the absence gracefully.

## 2. Drop in the reusable fixture

Copy the fixture into your test project (for example `tests/MyPlugin.Tests/PluginLoadFixture.cs`). The only thing you must customise is the plugin build path.

```csharp file=../../tests/Common.SampleTests/PluginLoadFixture.cs#streaming-plugin-fixture
```

The fixture looks for `plugins/MyPlugin/bin/Debug/net8.0/MyPlugin.dll`. Adjust the path to match your project layout or read it from an environment variable if your CI publishes elsewhere.

## 3. Add a smoke test and remove the skip once ready

```csharp file=../../tests/Common.SampleTests/PluginLoadFixture.cs#streaming-plugin-smoke-test
```

- Until your plugin build output exists, keep the `[Fact(Skip = ...)]` to prevent the fixture from running.
- After `dotnet publish` succeeds, remove the skip so the fixture loads the plugin and captures the DI container.
- From there, write targeted tests that resolve services and exercise indexer or download client logic.

## 4. Run the loop

```bash

# 1. Build the plugin into the location your fixture expects
 dotnet publish plugins/MyPlugin/MyPlugin.csproj -c Debug -f net8.0 -o plugins/MyPlugin/bin/Debug/net8.0

# 2. Execute tests (fixture loads the published plugin)
 dotnet test tests/MyPlugin.Tests/MyPlugin.Tests.csproj -c Debug

```

### Validation checklist
- [ ] Plugin assembly publishes next to its dependencies (set `CopyLocalLockFileAssemblies=true`).
- [ ] Fixture resolves `IServiceProvider` either via `IServiceProviderAccessor` or reflection fallback.
- [ ] Tests dispose the plugin so the collectible load context unloads cleanly (look for zero lingering handles).
- [ ] Integration tests assert at least one real call path (indexer, download client, or other service).

### Troubleshooting

| Symptom | Fix |
| --- | --- |
| `DirectoryNotFoundException` from the fixture | Ensure `dotnet publish` output matches the path in `buildOutput` (step 4). |
| `InvalidOperationException: Plugin must expose IServiceProvider` | Implement the sample `IServiceProviderAccessor` interface and forward to the base `Services` property. |
| Types from `Lidarr.Plugin.Abstractions` mismatch | Restore dependencies using the same Common version that built your plugin and rebuild. |

Once this harness passes, you have a safety net for every change to your streaming plugin.
