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
- Configure caching: the template includes a minimal `ICachePolicyProvider` (see `Policies/PolicyProvider.cs`) with sensible defaults and ETag revalidation for details.
- OAuth2: use `Auth/MyPluginOAuthSettings` to capture client credentials and redirect URI (for PKCE flows).
- Use `Lidarr.Plugin.Common.TestKit` stubs/fixtures to simulate providers.
