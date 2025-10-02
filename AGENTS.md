# Repository Guidelines

## Project Structure & Module Organization
- Core libraries live under `src/`: `Lidarr.Plugin.Common` (resilience, caching, downloads) and `Abstractions` (manifest/contracts). Multi-targeting is managed via `Directory.Build.props`.
- `tests/` hosts xUnit suites and isolation fixtures; run them with or without the optional CLI feature flag. `testkit/` exposes reusable HTTP gremlins and plugin harnesses for downstreams.
- CLI assets reside in `src/CLI`, while sample integrations are under `examples/`. Supporting docs and playbooks live in `docs/`; automation lives in `tools/` and `scripts/`.

## Build, Test, and Development Commands
- `dotnet build -c Release` — compiles all targets and enforces analyzers.
- `dotnet test -c Release --no-build` — runs the full suite; append `-p:IncludeCLIFramework=true` when validating CLI scenarios.
- `dotnet format src/Lidarr.Plugin.Common.csproj analyzers --verify-no-changes` — ensures analyzers (RS0016/RS0017, etc.) stay clean.
- `tools/Update-PublicApiBaselines.ps1` — regenerates per-TFM `PublicAPI` baselines after contract updates; commit the resulting files.

## Coding Style & Naming Conventions
- Follow the repo `.editorconfig`: 4-space indentation, UTF-8, `var` for obvious types, and PascalCase for public members. Private fields use `_camelCase`; locals remain `camelCase`.
- Prefer expression-bodied members for short delegates; avoid region blocks.
- Run `dotnet format` before pushing; markdown is linted with `.markdownlint.yaml` and Vale (`.vale/` rules).

## Testing Guidelines
- Tests are xUnit; name classes `*Tests` and methods `Should_*`. Put provider fixtures in `tests/Common.SampleTests` and isolation scenarios in `tests/Isolation`.
- Include negative-path coverage (timeouts, rate limits, cache misses) whenever adding resilience features.
- Record new golden payloads in `testkit/Fixtures` and document provenance in README comments.

## Commit & Pull Request Guidelines
- Use Conventional Commit prefixes (`feat:`, `fix:`, `chore:`) as reflected in `git log`.
- PRs must: describe behavioral changes, link issues, note config migrations, and attach `dotnet test` output (or GitHub Actions run). Include screenshots for CLI UX changes.
- Before opening a PR: run build, tests, analyzer verification, and regenerate API baselines if public surfaces changed. Ensure `ReportGenerator` artifacts upload cleanly.

## Tooling & Automation Tips
- GitHub Actions expect both .NET 6.0 and 8.0 SDKs; mirror that locally when debugging.
- Packaging is handled via `PluginPackaging.targets`; use `dotnet pack -c Release` to produce plugins and verify `dist/` outputs.
- Secrets and tokens must flow through the provided masking helpers (`IsSensitiveParameter`) before logging.
