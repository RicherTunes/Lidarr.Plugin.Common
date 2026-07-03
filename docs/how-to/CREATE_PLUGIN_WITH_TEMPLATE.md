# Create a Plugin with the Template

Get started in minutes using the project template and TestKit.

## Prerequisites
- .NET 8.0 SDK installed

## Install the template

From the repo root:

```bash
dotnet new install templates/lidarr-plugin
```

## Create a new plugin project

```bash
dotnet new lidarr-plugin -n MyPlugin
cd MyPlugin
```

This scaffolds:
- `plugin.json` — minimal host metadata copied next to the built DLL
- `src/MyPlugin` — minimal settings/module/indexer helper plus an `IPlugin` entrypoint
- `tests/MyPlugin.Tests` — request-builder smoke coverage plus a `PluginSandbox` load test

The generated project restores `Lidarr.Plugin.Common`,
`Lidarr.Plugin.Abstractions`, and `Lidarr.Plugin.Common.TestKit` from your
configured NuGet feeds. Until those packages are published to the feed you use,
build against a local Common checkout:

```bash
dotnet test tests/MyPlugin.Tests/MyPlugin.Tests.csproj -c Release \
  -p:LidarrPluginCommonRepoRoot=/path/to/Lidarr.Plugin.Common
```

## Run tests

```bash
dotnet test tests/MyPlugin.Tests/MyPlugin.Tests.csproj -c Release
```

## Next steps
- Implement real host-facing `IIndexer` and/or `IDownloadClient` adapters and
  return them from the generated `MyPluginPlugin`.
- Fill in API calls with `StreamingApiRequestBuilder` → Options → `ExecuteWithResilienceAsync`.
- Configure caching: the template includes a minimal `ICachePolicyProvider` (see `Policies/PolicyProvider.cs`) with sensible defaults and ETag revalidation for details.
- Configure `CachePolicyRegistry` in your host for sensible TTLs.
- OAuth2: use `Auth/MyPluginOAuthSettings` to capture client credentials and redirect URI (for PKCE flows).
- Use `Lidarr.Plugin.Common.TestKit` stubs/fixtures to simulate providers.
