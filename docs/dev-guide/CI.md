# Continuous Integration

This repository ships with a multi-stage GitHub Actions pipeline that keeps the shared library healthy and releasable.

## Code workflows

- **CI** (`.github/workflows/ci.yml`) runs on Ubuntu and Windows with .NET 6/8. It verifies formatting, builds, tests, gathers coverage, and runs a dry-pack.
- **PR validation** (`.github/workflows/pr-validation.yml`) adds targeted smoke checks for pull requests (fast feedback subset of CI).
- **Release** (`.github/workflows/release.yml`) publishes tagged artifacts to NuGet.
- **Docs** (`.github/workflows/docs.yml`) runs snippet verification, markdownlint, cspell, and lychee link checking whenever documentation changes.

## What CI enforces

- `dotnet format --verify-no-changes`
- `dotnet build -warnaserror`
- `dotnet test` for all TFMs with XPlat coverage
- `dotnet pack` (dry run) for both `Lidarr.Plugin.Common` and `Lidarr.Plugin.Common.TestKit`
- Public API analyzer baselines (`RS0016`, `RS0026`)
- Snippet extraction via `dotnet run --project tools/DocTools/SnippetVerifier`

## Coverage details

- Coverage collected via `coverlet.collector`
- Cobertura + TRX artifacts published per OS matrix entry
- Coverage summary comment fails the job if coverage < 60%, warns below 80%

## Releases

- Tagging `vX.Y.Z` triggers the Release workflow
- Builds/tests/packs before pushing to NuGet (`--skip-duplicate` keeps reruns safe)

## Local verification

1. `dotnet format --verify-no-changes`
2. `dotnet test -c Release --collect:"XPlat Code Coverage"`
3. `dotnet pack src/Lidarr.Plugin.Common.csproj -c Release /p:ContinuousIntegrationBuild=true`
4. `dotnet pack testkit/Lidarr.Plugin.Common.TestKit.csproj -c Release /p:ContinuousIntegrationBuild=true`
5. `dotnet run --project tools/DocTools/SnippetVerifier`
6. `pwsh tools/DocTools/lint-docs.ps1`
7. `pwsh tools/ManifestCheck.ps1 -ProjectPath plugins/<Plugin>/<Plugin>.csproj -ManifestPath plugins/<Plugin>/plugin.json`
8. `Import-Module ./tools/PluginPack.psm1; New-PluginPackage -Csproj plugins/<Plugin>/<Plugin>.csproj -Manifest plugins/<Plugin>/plugin.json`

## Maintenance

- Dependabot keeps Actions/NuGet dependencies current.
- Update `PublicAPI.*.txt` whenever the public surface changes.
- Keep docs automation in sync with [`docs/dev-guide/TESTING_DOCS.md`](TESTING_DOCS.md).



