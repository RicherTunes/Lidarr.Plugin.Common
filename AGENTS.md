# Repository Guidelines

## Project Structure & Module Organization
- src/ – core multi-targeted library (Lidarr.Plugin.Common.csproj) with optional CLI helpers in src/CLI/ (add -p:IncludeCLIFramework=true to include them).
- src/Abstractions/ – host-owned contracts with enforced PublicAPI baselines (PublicAPI/net6.0|net8.0).
- 	estkit/ – reusable testing harness packaged as Lidarr.Plugin.Common.TestKit.
- 	ests/ – xUnit suites (one project) covering cache, HTTP, CLI, and isolation scenarios.
- docs/, xamples/, 	ools/ – contributor docs, sample plugins, and automation scripts (e.g., 	ools/Update-PublicApiBaselines.ps1).

## Build, Test, and Development Commands
- dotnet build -c Release – restores, multi-target builds, and packs the core library (SourceLink enabled).
- dotnet test -c Release – runs all library + integration tests; coverage is collected via coverlet.
- dotnet test -c Release -p:IncludeCLIFramework=true – required when editing CLI modules so the System.CommandLine + Spectre stack is compiled.
- dotnet pack src/Lidarr.Plugin.Common.csproj -c Release -p:IncludeCLIFramework=true – produces CLI-enabled nupkgs if you need them locally.
- pwsh tools/Update-PublicApiBaselines.ps1 – regenerates PublicAPI shipped/unshipped files after intentional surface changes.

## Coding Style & Naming Conventions
- C# 10/12, 4-space indentation, file-scoped namespaces where practical.
- PascalCase for types/namespaces; camelCase for locals and parameters; async methods end with Async.
- Nullable reference types are enabled; avoid #nullable disable unless justified.
- Keep multi-target differences in Compat/net6|net8/ partials instead of inline #if blocks.
- Analyzers: Microsoft.CodeAnalysis.PublicApiAnalyzers (treat as errors) and PackageValidation in CI.

## Testing Guidelines
- Framework: xUnit (	ests/Lidarr.Plugin.Common.Tests.csproj). Test files named *Tests.cs; individual methods follow MethodUnderTest_State_Expectation.
- Run dotnet test -c Release before every PR; add the CLI property when touching CLI code.
- Update or add tests alongside features/bug fixes; ensure coverage for cancellation paths and multi-TFM code.

## Commit & Pull Request Guidelines
- Commit messages: short imperative summary (e.g., Fix config reset persistence). Bundle related changes only.
- PRs must pass GitHub Actions (ci.yml), include rationale in the description, link issues when available, and update CHANGELOG.md + PublicAPI baselines when altering public surface.
- Provide reproduction steps or screenshots for CLI/UX changes; flag breaking changes clearly.
