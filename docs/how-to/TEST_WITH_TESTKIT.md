# Test Plugins with the Common TestKit

The `Lidarr.Plugin.Common.TestKit` package bundles the fixture, HTTP simulators, and sample payloads that the core library uses to harden plugins. Add it to your test project and you can exercise real `IPlugin` entry points inside a collectible AssemblyLoadContext without rebuilding that plumbing.

## 1. Reference the TestKit

```xml
<ItemGroup>
  <ProjectReference Include="..\testkit\Lidarr.Plugin.Common.TestKit.csproj" />
  <ProjectReference Include="..\src\Lidarr.Plugin.Common.csproj" />
  <ProjectReference Include="..\src\Abstractions\Lidarr.Plugin.Abstractions.csproj" />
</ItemGroup>
```

When you publish the TestKit to NuGet, replace the `ProjectReference` with a `PackageReference` targeting the latest version.

## 2. Load a plugin in isolation

Use `PluginSandbox` to load a compiled plugin (or one emitted on the fly) inside a private `AssemblyLoadContext`:

```csharp
using Lidarr.Plugin.Common.TestKit.Fixtures;

await using var sandbox = await PluginSandbox.CreateAsync(pluginPath);
var indexer = await sandbox.CreateIndexerAsync();
var downloadClient = await sandbox.CreateDownloadClientAsync();
```

`PluginSandbox` takes care of unloading the context and running the required garbage-collection cycles on dispose so Windows file locks are released.

## 3. Deserialize manifest and settings fixtures

Embedded JSON payloads live under `TestKit/Data`. Retrieve them via `EmbeddedJson` for repeatable deserialization tests:

```csharp
using Lidarr.Plugin.Common.TestKit.Data;

using var tidalPreview = EmbeddedJson.Open("Tidal/track-preview.json");
Assert.True(tidalPreview.RootElement.GetProperty("mediaMetadata").GetProperty("previewAvailable").GetBoolean());
```

## 4. Exercise HTTP edge cases

Each handler wraps a common upstream behaviour:

| Handler | Scenario |
|---------|----------|
| `GzipMislabeledHandler` | Returns gzipped bytes without a `Content-Encoding` header. |
| `RetriableFlakyHandler` | Emits one or more 429/503 responses with optional `Retry-After`. |
| `PartialContentHandler` | Supports range requests and optional first-range failure. |
| `SlowStreamHandler` | Drips bytes with a configurable delay to test cancellation. |
| `PreviewStreamHandler` | Tags responses as previews via custom headers. |
| `JsonProblemHandler` | Serves `application/problem+json` payloads. |

Example usage:

```csharp
using var handler = new RetriableFlakyHandler(failureCount: 2, retryAfter: TimeSpan.FromSeconds(1));
using var client = new HttpClient(handler, disposeHandler: true);
var response = await client.GetAsync("https://api.example.test/ping");
```

## 5. Assert results consistently

`PluginAssertions` contains reusable guards for download results and quality fallback checks:

```csharp
using Lidarr.Plugin.Common.TestKit.Assertions;

PluginAssertions.AssertSuccess(result);
PluginAssertions.AssertImplicitFallback(requestedQuality, actualQuality);
```

## 6. Capture host logs during tests

`PluginTestContext` provides a `TestLogSink` that records every log entry when the host does not supply its own `ILoggerFactory`:

```csharp
var context = new PluginTestContext(new Version(2, 14, 0));
await plugin.InitializeAsync(context);
Assert.Contains(context.LogEntries.Snapshot(), entry => entry.Level == LogLevel.Error);
```

Dispose the context when finished so the in-memory logger factory is released.

## 7. Recommended assertions in CI

* Load two plugins with different `Lidarr.Plugin.Common` versions through `PluginSandbox` to ensure side-by-side compatibility.
* Use `SlowStreamHandler` plus a short cancellation token timeout to confirm downloads honour per-request cancellation.
* Validate manifest metadata using the shared `ManifestCheck.ps1` script (see `docs/how-to/PACKAGE_PLUGIN.md`).

With these helpers your plugin tests can focus on behaviour instead of ceremony, while still matching the isolation guarantees enforced by the host loader.
