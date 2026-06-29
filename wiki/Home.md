# Lidarr.Plugin.Common — Wiki

**Lidarr.Plugin.Common** is the shared library that underpins the RicherTunes Lidarr streaming/AI plugins — Amazonmusicarr, Applemusicarr, Brainarr, Qobuzarr, and Tidalarr.
It ships resilience policies, HTTP helpers, bridge contracts, packaging tooling, and a TestKit so every plugin follows the same canonical patterns.
Each plugin vendors Common as `ext/Lidarr.Plugin.Common`, then ILRepack-merges and internalizes Common into the packaged plugin DLL loaded by Lidarr. The host-owned ABI lives in `Lidarr.Plugin.Abstractions`.

This wiki is for **plugin authors** building on Common.
Its job is orientation — what exists, where to find it, and where the canonical docs live.

## Wiki pages

| Page | Scope |
|------|-------|
| [Architecture Overview](./Architecture-Overview.md) | ALC isolation, bridge runtime, layer diagram |
| [SDK and Extension Points](./SDK-and-Extension-Points.md) | `StreamingPlugin<TModule,TSettings>`, DI seams, provider interfaces |
| [Shared Helpers Catalog](./Shared-Helpers-Catalog.md) | Resilience, HTTP defaults, collections, validation, diagnostics |
| [Ecosystem Parity and Guards](./Ecosystem-Parity-and-Guards.md) | Parity matrix, promotion checklist, drift enforcement |
| [Versioning and Submodule Pinning](./Versioning-and-Submodule-Pinning.md) | Version contract, submodule workflow, upgrade checklist |
| [Testing with the TestKit](./Testing-with-the-TestKit.md) | TestKit fixtures, HTTP handlers, ALC harness |
| [CI and Packaging](./CI-and-Packaging.md) | `PluginPack.psm1`, Gitea-primary CI, shared lint/verify gates |

## Key references

All links point to canonical documentation in `docs/`:

- [FAQ for Plugin Authors](../docs/FAQ_FOR_PLUGIN_AUTHORS.md) — common pitfalls (duplicate assemblies, API baselining, NuGet feeds).
- [Architecture Status](../docs/ARCHITECTURE_STATUS.md) — bridge-runtime migration baseline and current state.
- [Abstractions](../docs/ABSTRACTIONS.md) — host-owned ABI (`IPlugin`, `IIndexer`, `IDownloadClient`).
- [Plugin Bridge](../docs/PLUGIN_BRIDGE.md) — `StreamingPlugin<TModule,TSettings>` quickstart and extension model.
- [Plugin Isolation](../docs/PLUGIN_ISOLATION.md) — how plugins load inside dedicated AssemblyLoadContexts.
- [Glossary](../docs/GLOSSARY.md) — shared terminology across the ecosystem.
- [Ecosystem Parity Matrix](../docs/ECOSYSTEM_PARITY_MATRIX.md) — single source of truth for cross-plugin pattern parity.
- [Ecosystem Version Contract](../docs/ECOSYSTEM_VERSION_CONTRACT.md) — version-coordination rules and `commonVersion` enforcement.
- [Testing with the TestKit](../docs/TESTING_WITH_TESTKIT.md) — fixtures, HTTP handlers, manifest helpers.
- [Packaging](../docs/PACKAGING.md) — `PluginPack.psm1` standardised publish flow.
- [Compatibility](../docs/COMPATIBILITY.md) — supported Common / Abstractions / host version combinations.
- [Upgrading](../docs/UPGRADING.md) — per-release bump checklist for downstream plugin repos.
