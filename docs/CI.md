# Continuous Integration

This repository ships with a multi-stage GitHub Actions pipeline that keeps the shared library healthy and releasable.

## Pull Requests and pushes to `main`

- `CI` workflow runs on Ubuntu and Windows runners with .NET 6 and 8 SDKs installed.
- `dotnet format --verify-no-changes` and `dotnet build -warnaserror` guard formatting, analyzers, and warnings.
- `dotnet test` executes for all target frameworks with `XPlat Code Coverage`; TRX and Cobertura XML are uploaded as artifacts.
- Test results are published back to the PR via annotations; coverage summaries appear directly in the PR conversation.
- A dependent `Pack (dry run)` job uses `dotnet pack src/Lidarr.Plugin.Common.csproj -c Release` to ensure the NuGet package continues to produce successfully and uploads the `.nupkg` artifact.

## Coverage details

- Coverage is collected automatically via `coverlet.collector` in each test project.
- Cobertura XML and TRX files are stored as build artifacts per OS matrix entry.
- The coverage summary comment fails the job if coverage drops below 60% and marks as warning below 80%.

## Releases

- Tagging `vX.Y.Z` triggers the `Release` workflow.
- The job restores, builds, tests, and packs the library before pushing the resulting package to NuGet.org using `NUGET_API_KEY`.
- Publishing is idempotent thanks to `--skip-duplicate`; re-running the workflow on the same tag is safe.

## Maintenance

- Dependabot checks NuGet dependencies and GitHub Action versions weekly; merge updates to keep the pipeline current.
- Public API changes must be recorded in `src/PublicAPI.Unshipped.txt` and moved to `PublicAPI.Shipped.txt` when released. The analyzer turns missing entries into build failures.

## Local verification tips

1. Run `dotnet format --verify-no-changes` before pushing.
2. Execute `dotnet test -c Release --collect:"XPlat Code Coverage"` to confirm tests & coverage locally.
3. Validate packing via `dotnet pack src/Lidarr.Plugin.Common.csproj -c Release /p:ContinuousIntegrationBuild=true`.
