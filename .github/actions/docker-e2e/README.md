# Docker E2E (Lidarr Plugin) — composite action

<!-- docval:ignore-script-refs: references plugin-side / proposed scripts, not Common tooling -->

Runs the wave-21/22 Docker E2E smoke harness for any Lidarr plugin that uses
`Lidarr.Plugin.Common.TestKit.Hosting.LidarrContainerFixture`. Boots the
plugin inside a real Lidarr container (via the per-plugin fixture), runs the
xUnit facts gated on `Category=DockerE2E`, and on failure uploads the
container logs and TRX results.

## When to use it

- You have a plugin that subclasses `LidarrContainerFixture` (wave 22a) and
  has at least one `DockerE2ETests.cs` with `[Trait("Category","DockerE2E")]`.
- You want a CI gate that proves the plugin actually loads inside Lidarr,
  not just that it compiles.

The plugin DLL **must already be built** (Release by default) before this
action runs — the action invokes `dotnet test --no-build`. The standard
build step is `pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests`.

## Inputs

| Name | Required | Default | Description |
|---|---|---|---|
| `plugin-name` | yes | — | Plugin slug. Used for artifact naming and to derive the default container name (`<plugin-name>-e2e`). |
| `test-project` | yes | — | Path to the test csproj containing DockerE2E facts. |
| `lidarr-docker-version` | no | `nightly-3.1.3.4970` | Lidarr container tag (plugins branch, net8). |
| `configuration` | no | `Release` | Build configuration. Must match the build step (we pass `--no-build`). |
| `test-filter` | no | `Category=DockerE2E` | xUnit filter expression. |
| `container-name` | no | `<plugin-name>-e2e` | Override the docker container name used for log capture. |
| `test-session-timeout-ms` | no | `600000` | xUnit `RunConfiguration.TestSessionTimeout` in ms. |

## Pre-conditions

1. Runner OS must be **Linux** (`ubuntu-latest`). Windows runners cannot
   execute Linux Docker images.
2. `actions/setup-dotnet` (8.0.x) must have run before this action.
3. The plugin DLL must already be built into `src/<Plugin>/bin/...`. The
   wave-21/22 fixture searches `src/<Plugin>/bin/` then `bin/Release` then
   `bin/Debug`.
4. The submodule `ext/Lidarr.Plugin.Common` must be checked out so this
   action's path is resolvable.

## Usage from a downstream plugin

Because the action lives inside the common submodule, downstream repos
reference it by submodule-relative path — no GitHub Actions Marketplace
publishing required.

```yaml
jobs:
  docker-e2e:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x

      - name: Build plugin
        shell: pwsh
        run: pwsh scripts/verify-local.ps1 -SkipExtract -SkipTests

      - name: Docker E2E
        uses: ./ext/Lidarr.Plugin.Common/.github/actions/docker-e2e
        with:
          plugin-name: tidalarr
          test-project: tests/Tidalarr.Tests/Tidalarr.Tests.csproj
```

For pinning to a specific common SHA, downstream plugins simply update the
submodule pointer (`ext-common-sha.txt` + `git submodule update`); the
checkout step picks up the pinned action automatically.

## Failure artifacts

On failure (any test red, or container failed to start) the action uploads
`docker-e2e-<plugin-name>`:

- `TestResults/<plugin-name>/docker-e2e.trx` — xUnit results
- `TestResults/<plugin-name>/container-logs/<container>.stdout.log`
- `TestResults/<plugin-name>/container-logs/<container>.stderr.log`
- `TestResults/<plugin-name>/container-logs/<container>.inspect.json`

Retention: 7 days.

## Roadmap

- Wave 23 — first adopter: tidalarr.
- Wave 24 — replicate adoption to qobuzarr, applemusicarr, brainarr.
