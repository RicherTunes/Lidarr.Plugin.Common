# Lidarr.Plugin.Common.TestKit

Shared testing helpers for Lidarr streaming plugins.

## Features

- `PluginSandbox` fixture that loads a compiled plugin inside a collectible AssemblyLoadContext and disposes it cleanly, making it easy to test side-by-side versions.
- Lightweight `PluginTestContext` with a captured log sink so plugin tests can assert on host logs without real host infrastructure.
- Battle-tested `HttpMessageHandler` shims for the most common upstream behaviours (mislabelled gzip, flaky retries, partial content resumptions, slow streams, preview/sample markers, and problem+json errors).
- Embedded JSON payloads covering tricky streaming metadata (multidisc Qobuz albums, Tidal preview tracks, unicode/emoji artists) for repeatable parsing tests.
- Assertion helpers for download outcomes, quality fallback checks, and preview rejection.

## Getting Started

```xml
<ItemGroup>
  <PackageReference Include="Lidarr.Plugin.Common.TestKit" Version="1.1.4" />
</ItemGroup>
```

Then in your test project:

```csharp
await using var sandbox = await PluginSandbox.CreateAsync(pluginPath);
var indexer = await sandbox.CreateIndexerAsync();
```

See the library documentation under `docs/how-to/TEST_WITH_TESTKIT.md` for full guidance.
