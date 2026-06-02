# Lidarr.Plugin.Common

Shared utilities, resilience policies, and packaging helpers for Lidarr streaming plugins. `Lidarr.Plugin.Abstractions` stays host-owned; each plugin ships its own copy of `Lidarr.Plugin.Common` inside a private AssemblyLoadContext.

[![Build Status](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/ci.yml/badge.svg)](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions)
[![Docs Status](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/docs.yml/badge.svg)](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/docs.yml)
[![Release CI](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/release.yml/badge.svg)](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/release.yml)
[![NuGet (Common)](https://img.shields.io/nuget/v/Lidarr.Plugin.Common?logo=nuget)](https://www.nuget.org/packages/Lidarr.Plugin.Common)
[![NuGet (Abstractions)](https://img.shields.io/nuget/v/Lidarr.Plugin.Abstractions?logo=nuget)](https://www.nuget.org/packages/Lidarr.Plugin.Abstractions)

## Quick start

| Role | Start here |
|------|-----------|
| Plugin author | [Quickstart: Build a Plugin](docs/quickstart/PLUGIN_AUTHOR.md) |
| Host maintainer | [Quickstart: Load Plugins Safely](docs/quickstart/HOST_MAINTAINER.md) |
| Library contributor | [Developer guide](docs/dev-guide/DEVELOPER_GUIDE.md) |

## Key capabilities

- **ALC isolation** — each plugin loads Common in a private `AssemblyLoadContext`; the host shares `Lidarr.Plugin.Abstractions` and two `Microsoft.Extensions` abstractions. → [Plugin Isolation](docs/PLUGIN_ISOLATION.md)
- **Streaming plugin bridge** — `StreamingPlugin<TModule, TSettings>` base class wires DI, settings, lifecycle, and the host bridge. → [Plugin Bridge](docs/PLUGIN_BRIDGE.md)
- **Resilient HTTP pipeline** — builder → options → executor → cache flow with retries, deadlines, concurrency caps, and request deduplication. → [HTTP Flow](docs/Flow.md) · [Key Services](docs/reference/KEY_SERVICES.md)
- **Security helpers** — `PathTraversalGuard`, `SecureMemory`, `TokenProtectorFactory`, LLM prompt sanitization. → [Shared Helpers Catalog](wiki/Shared-Helpers-Catalog.md)
- **TestKit** — reusable test fixtures, HTTP handlers, ALC harness, and ecosystem-parity guard tests. → [Testing with the TestKit](docs/TESTING_WITH_TESTKIT.md)
- **Packaging** — `PluginPack.psm1` standardises build, manifest validation, and ZIP packaging for every plugin release. → [Packaging](docs/PACKAGING.md)
- **Ecosystem parity** — mechanical guard tests enforce that all plugins follow the same canonical patterns. → [Ecosystem Parity and Guards](wiki/Ecosystem-Parity-and-Guards.md)

## What's New

Latest: v1.17.0 — May 25, 2026

- **v1.17.0** — Wave-21 parity helpers (PathTraversalGuard.ContainsTraversalAttempt probe, AlbumDownloadUri parser, AlbumReleaseInfoBuilder Edition/Explicit/Live slots, unified plugin version-bump helper).
- **v1.16.0** — `SlidingWindowAuthFailureHandler` (K-of-N-in-W sliding-window circuit semantics, sibling of `DefaultAuthFailureHandler`); unblocks brainarr's `LlmAuthCircuit` convergence onto the shared Common stack.
- **v1.15.0** — `BoundedConcurrentDictionary` richer API surface (indexer setter, `ContainsKey`, `Values`, `IEnumerable<KeyValuePair>`); SecureMemory + Conservative rate-limit profile + PagedResponseValidator.
- **v1.14.x** — AuthFailureGate surface (registry, delegating handler, gated exception) + multi-plugin ALC coexistence proof + ecosystem version contract enforcement.
- **v1.13.x** — Plugin packaging contract + plugin version contract + published-release installability checks (test kit).
- **v1.12.0** — `AlbumReleaseInfoBuilder` (lift wave A item 8); `HostBridgeDownloadOrchestrator` (lift wave A item 2); `RetryPolicyOptions.ForLocalProviders` preset.
- **v1.8.0** — ecosystem version contract via `versionContract` section in `scripts/parity-spec.json`; `forbiddenFields` enforcement wired into parity-lint; ALC multi-plugin co-existence fix.

Release notes: [v1.17.0](https://github.com/RicherTunes/Lidarr.Plugin.Common/releases/tag/v1.17.0) · [v1.16.0](https://github.com/RicherTunes/Lidarr.Plugin.Common/releases/tag/v1.16.0)

Ecosystem-wide parity (every plugin × every cross-cutting concern): see [docs/ECOSYSTEM_PARITY_MATRIX.md](docs/ECOSYSTEM_PARITY_MATRIX.md).

## Choose your adventure

### Plugin authors

- [Create a plugin project](docs/how-to/CREATE_PLUGIN.md)
- [Use the streaming plugin bridge](docs/PLUGIN_BRIDGE.md)
- [Test a streaming plugin end-to-end](docs/how-to/USE_STREAMING_PLUGIN.md)
- [Map settings with the bridge](docs/SETTINGS_PROVIDER.md)
- [Understand the HTTP flow (Builder → Options → Executor → Cache)](docs/Flow.md)
- [Key services and utilities](docs/reference/KEY_SERVICES.md)
- [Manage sessions/tokens](docs/how-to/TOKEN_MANAGER.md)
- [Test with the shared TestKit](docs/TESTING_WITH_TESTKIT.md)
- [Package with PluginPack.psm1](docs/PACKAGING.md)
- [FAQ for plugin authors](docs/FAQ_FOR_PLUGIN_AUTHORS.md)
- [Bump the submodule in downstream repos](docs/how-to/BUMP_SUBMODULE.md)

### Host maintainers

- [AssemblyLoadContext isolation](docs/PLUGIN_ISOLATION.md)
- [Plugin manifest schema & validation](docs/PLUGIN_MANIFEST.md)
- [Compatibility matrix & EOL policy](docs/COMPATIBILITY.md)
- [Migration checklist for legacy plugins](docs/migration/PLUGIN_MIGRATION.md)

### Library contributors

- [Abstractions overview & API baselines](docs/ABSTRACTIONS.md)
- [Developer guide](docs/dev-guide/DEVELOPER_GUIDE.md)
- [Docs & tooling guide](docs/dev-guide/TESTING_DOCS.md)
- [Packaging playbook](docs/PACKAGING.md)
- [Upgrade checklist for releases](docs/UPGRADING.md)

## Wiki

Orientation pages that map what exists and where to find it — full details live in the linked `docs/`.

| Page | Scope |
|------|-------|
| [Home](wiki/Home.md) | Wiki index and orientation for plugin authors |
| [Architecture Overview](wiki/Architecture-Overview.md) | ALC isolation, bridge runtime, layer diagram |
| [SDK and Extension Points](wiki/SDK-and-Extension-Points.md) | `StreamingPlugin<TModule,TSettings>`, DI seams, provider interfaces |
| [Shared Helpers Catalog](wiki/Shared-Helpers-Catalog.md) | Security, resilience, HTTP defaults, collections, validation |
| [Ecosystem Parity and Guards](wiki/Ecosystem-Parity-and-Guards.md) | Parity matrix, promotion checklist, drift enforcement |
| [Versioning and Submodule Pinning](wiki/Versioning-and-Submodule-Pinning.md) | Version contract, submodule workflow, upgrade checklist |
| [Testing with the TestKit](wiki/Testing-with-the-TestKit.md) | TestKit fixtures, HTTP handlers, ALC harness |
| [CI and Packaging](wiki/CI-and-Packaging.md) | `PluginPack.psm1`, reusable workflows, SHA pins |

## Documentation workflow

- Markdown lives under `docs/`; every page is the single source for its topic.
- Code samples use snippet includes (` ```csharp file=...`) verified by `tools/DocTools/SnippetVerifier`.
- Run the docs toolchain locally:

  ```bash
  # lint + spell check + links + snippets
  gh workflow run docs.yml --ref <branch>
  # or run manually
  dotnet run --project tools/DocTools/SnippetVerifier
  ```

- GitHub Actions `docs.yml` enforces markdownlint, cspell, and link checking on every documentation PR; Vale prose-linting and snippet compilation also run but are non-blocking (`continue-on-error`).

## Repository layout

- `src/` – library source (`Lidarr.Plugin.Common`, `Lidarr.Plugin.Abstractions`).
- `tests/` – unit/integration tests.
- `examples/` – sample hosts, plugin bridge snippets, manifest helpers.
- `docs/` – knowledge base described above.
- `tools/` – Snippet verifier, manifest/package automation, docs lint scripts.

## Documentation

- [Changelog](CHANGELOG.md)
- [Contributing](CONTRIBUTING.md)
- [Security](SECURITY.md)
- [Glossary](docs/GLOSSARY.md)
- [Docs directory](docs/)

## License

MIT – see [LICENSE](LICENSE).

Note: All product and company names (e.g., Qobuz, TIDAL, Spotify) are trademarks of their respective owners; usage here is for descriptive purposes only and does not imply endorsement.
