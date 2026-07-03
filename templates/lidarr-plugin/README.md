# MyPlugin

Generated from the RicherTunes Lidarr streaming plugin template.

## Build

The generated projects reference these packages by default:

- `Lidarr.Plugin.Common`
- `Lidarr.Plugin.Abstractions`
- `Lidarr.Plugin.Common.TestKit`

Those packages must be available from one of your configured NuGet sources before
`dotnet build` or `dotnet test` can restore this scaffold.

When developing against a local Common checkout, pass the repo root and the
projects will use `ProjectReference` instead of package restore:

```powershell
dotnet build src/MyPlugin/MyPlugin.csproj -p:LidarrPluginCommonRepoRoot=/path/to/Lidarr.Plugin.Common
dotnet test tests/MyPlugin.Tests/MyPlugin.Tests.csproj -p:LidarrPluginCommonRepoRoot=/path/to/Lidarr.Plugin.Common
```

The first-party RicherTunes plugin ecosystem currently vendors Common as
`ext/Lidarr.Plugin.Common` and validates the submodule pin in CI. Use that path
until the Common, Abstractions, and TestKit packages are published to the feed you
intend to restore from.

The scaffold includes a minimal `IPlugin` entrypoint and `plugin.json` so the
generated tests can load it in an isolated `PluginSandbox`. It does not implement a
host-facing Lidarr indexer or download client yet; fill those adapters in before
packaging for a real Lidarr installation.
