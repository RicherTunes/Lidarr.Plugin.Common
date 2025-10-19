# Lidarr.Plugin.Common

Shared utilities, resilience policies, and packaging helpers for Lidarr streaming plugins. `Lidarr.Plugin.Abstractions` stays host-owned; each plugin ships its own copy of `Lidarr.Plugin.Common` inside a private AssemblyLoadContext.

[![Build Status](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/ci.yml/badge.svg)](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions)
[![Docs Status](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/docs.yml/badge.svg)](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/docs.yml)
[![Release CI](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/release.yml/badge.svg)](https://github.com/RicherTunes/Lidarr.Plugin.Common/actions/workflows/release.yml)

## What's New

Latest: v1.1.6 - October 11, 2025

- Diagnostics: `PluginOperationResultJson` helper for consistent structured output.
- HTTP cache: 304 revalidation path hardened; short stale grace prevents TTL-edge races.
- Windows token store: atomic writes via unique temp files + short retries to avoid sharing violations.
- Resilience: honor `Retry-After` absolute dates without jitter (still clamped by retry budget).

Release notes: [v1.1.6](https://github.com/RicherTunes/Lidarr.Plugin.Common/releases/tag/v1.1.6)

## Security & Conduct

- Security policy: see SECURITY.md for supported versions and how to report vulnerabilities.
- Code of Conduct: this project follows the Contributor Covenant 2.1.

## Legal Notes

All brand names and trademarks referenced (e.g., Qobuz, TIDAL, Spotify) are the property of their respective owners. This project is an independent community effort and is not affiliated with or endorsed by those companies.

## Choose your adventure

### Plugin authors
- [Create a plugin project](docs/how-to/CREATE_PLUGIN.md)
- [Use the streaming plugin bridge](docs/PLUGIN_BRIDGE.md)
- [Test a streaming plugin end-to-end](docs/how-to/USE_STREAMING_PLUGIN.md)
- [Map settings with the bridge](docs/SETTINGS_PROVIDER.md)
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
- [TagLib dependency modes (public vs. private)](docs/dev-guide/TAGLIB_DEPENDENCY.md)

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
- GitHub Actions `docs.yml` enforces markdownlint, Vale, cspell, link checking, and snippet compilation on every documentation PR.

## Repository layout
- `src/` – library source (`Lidarr.Plugin.Common`, `Lidarr.Plugin.Abstractions`).
- `tests/` – unit/integration tests.
- `examples/` – sample hosts, plugin bridge snippets, manifest helpers.
- `docs/` – knowledge base described above.
- `tools/` – Snippet verifier, manifest/package automation, docs lint scripts.

## License
MIT – see [LICENSE](LICENSE).
