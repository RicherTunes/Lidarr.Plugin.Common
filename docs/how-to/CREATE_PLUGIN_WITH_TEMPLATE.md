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
- `src/MyPlugin` — minimal settings/module/indexer
- `tests/MyPlugin.Tests` — one passing xUnit test

## Run tests

```bash
dotnet test tests/MyPlugin.Tests/MyPlugin.Tests.csproj -c Release
```

## Next steps
- Fill in API calls with `StreamingApiRequestBuilder` → Options → `ExecuteWithResilienceAsync`.
- Configure `CachePolicyRegistry` in your host for sensible TTLs.
- Use `Lidarr.Plugin.Common.TestKit` stubs/fixtures to simulate providers.

