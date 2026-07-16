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

## Ecosystem parity guard

`tests/MyPlugin.Tests/MyPluginEcosystemParityTests.cs` subclasses Common's
`EcosystemParityTestBase` — the same structural + behavior contract every
RicherTunes plugin repo runs (`dotnet test --filter "Category=Parity"`). It checks
`Directory.Build.props`, `Directory.Packages.props`, `plugin.json`, `global.json`,
and — because the class overrides `PluginAssembly` — the behavior contracts (no
plugin-local token-store/response-cache/config-path forks, `AddBridgeDefaults()`
registered, file↔class name parity, ...). Without the `PluginAssembly` override,
14 of the 16 behavior checks silently skip; never remove it to silence a red build.

## Continuous Integration

The scaffold ships both CI configurations used across the ecosystem:

- `.gitea/workflows/ci.yml` — Gitea-primary: secret scan (gitleaks, integrity-pinned),
  Common submodule pin guard, Common's shared lint-gate runner, a dependency-CVE
  scan (`check-vulnerable-packages.ps1`, fails on High+), and a verify job
  (build + full test suite, including the parity guard).
- `.github/workflows/ci.yml` — a mirror of the same jobs, each guarded with
  `if: github.server_url == 'https://github.com'` so a Gitea instance that also
  reads `.github/workflows` never double-runs them. Keep both files in sync —
  patch them in the same PR.

**Prerequisite — the workflows do not pass until you vendor Common.** The lint,
pin-guard, dependency-scan, and verify jobs call scripts from the Common submodule,
which a freshly generated repo does not have yet:

```bash
git submodule add <common-repo-url> ext/Lidarr.Plugin.Common
git rev-parse HEAD:ext/Lidarr.Plugin.Common > ext-common-sha.txt
```

Known limitation: Common's template smoke test materializes this scaffold and runs
`dotnet build` + `dotnet test` (parity guard included) against locally packed
Common/TestKit packages, and asserts the workflow files materialize as valid YAML —
but it cannot execute the workflows themselves (no submodule, no CI runner in the
smoke environment). The first real push to your Gitea/GitHub repo is the first
live run.
