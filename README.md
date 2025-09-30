# Lidarr.Plugin.Common

Shared utilities, patterns, and resilient infrastructure for building Lidarr streaming plugins. The library complements the host-owned `Lidarr.Plugin.Abstractions` contract so each plugin can focus on service-specific logic.

[![Build Status](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/ci.yml/badge.svg)](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions)
[![NuGet Version](https://img.shields.io/nuget/v/Lidarr.Plugin.Common.svg)](https://www.nuget.org/packages/Lidarr.Plugin.Common/)
[![Downloads](https://img.shields.io/nuget/dt/Lidarr.Plugin.Common.svg)](https://www.nuget.org/packages/Lidarr.Plugin.Common/)

## Packages at a glance

- **`Lidarr.Plugin.Abstractions`** – Stable host ABI. Interfaces, DTOs, and loader helpers that must be shared across AssemblyLoadContexts. See the [Abstractions overview](docs/reference/ABSTRACTIONS.md).
- **`Lidarr.Plugin.Common`** – Plugin-owned implementation helpers (HTTP, retry policies, orchestration, caching). Each plugin ships its own version inside its private AssemblyLoadContext. See the [architecture guide](docs/concepts/ARCHITECTURE.md).

## Choose your path

- **Building a plugin?** Start with the [plugin author quick start](docs/quickstart/PLUGIN_AUTHOR.md) and task guides in [`docs/how-to`](docs/how-to/).
- **Maintaining the host?** Review [plugin isolation](docs/concepts/PLUGIN_ISOLATION.md), the [manifest reference](docs/reference/MANIFEST.md), and the [compatibility matrix](docs/concepts/COMPATIBILITY.md).
- **Contributing to the library?** Follow the [developer guide](docs/dev-guide/DEVELOPER_GUIDE.md), [release policy](docs/dev-guide/RELEASE_POLICY.md), and [docs automation](docs/dev-guide/TESTING_DOCS.md).

## Quick start (plugin project)

```bash

# Add the shared helpers to your plugin
dotnet add package Lidarr.Plugin.Common

# Reference the host ABI for compile-time contracts only
dotnet add package Lidarr.Plugin.Abstractions --version 1.0.0

```

Recommended `.csproj` fragment:

```xml

<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Lidarr.Plugin.Abstractions" Version="1.0.0" PrivateAssets="all" ExcludeAssets="runtime;native;contentfiles" />
  <PackageReference Include="Lidarr.Plugin.Common" Version="1.1.4" />
</ItemGroup>

```

See [CREATE_PLUGIN.md](docs/how-to/CREATE_PLUGIN.md) for the full walkthrough and the [isolation host sample](docs/examples/ISOLATION_HOST_SAMPLE.md) for loading plugins in collectible AssemblyLoadContexts.

## Documentation
All documentation lives under [`docs/`](docs/README.md) and is organised as a knowledge base:

- **Quick start** – choose a persona-specific onboarding flow.
- **Concepts** – isolation, architecture, compatibility, and versioning invariants.
- **How-to** – task-focused guides (logging, OAuth, orchestrators, etc.).
- **Reference** – manifest schema, settings, and public API baseline policy.
- **Migration** – legacy upgrades and breaking-change archive.
- **Dev guide** – contribution standards, CI, release process, and documentation tooling.

## Tooling & verification

- `dotnet build` / `dotnet test` – standard library verification across net6.0 and net8.0.
- `dotnet run --project tools/DocTools/SnippetVerifier` – compiles all tagged documentation snippets.
- `pwsh tools/DocTools/lint-docs.ps1` – runs markdown lint, spell check, and link validation.
- GitHub Actions `ci.yml` (code) and `docs.yml` (documentation) enforce the same checks in CI.

## Repository layout

- `src/` – library source (Common + Abstractions).
- `examples/` – runnable samples and plugin scaffolding.
- `tests/` – unit and integration tests.
- `docs/` – documentation hub (see [`docs/README.md`](docs/README.md)).
- `tools/` – repo tooling (snippet verifier, docs lint script).

## License
Licensed under the MIT License. See [LICENSE](LICENSE).

