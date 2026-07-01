# Lidarr.Plugin.Abstractions

`Lidarr.Plugin.Abstractions` is the host-owned Application Binary Interface (ABI). It defines the contracts that cross AssemblyLoadContexts and must remain stable across plugin updates.

## Core interfaces

- **`IPlugin`** – lifecycle entry point (`InitializeAsync`, factories for indexers/download clients, disposal).
- **`IPluginContext`** – host services (versions, logging, clock) supplied during plugin initialization.
- **`IIndexer`** – exposes search operations the host calls to discover releases.
- **`IDownloadClient`** – hands back download handles or streams to the host orchestrator.
- **`ISettingsProvider`** – type-safe access to plugin settings supplied by the host UI.

XML documentation for each interface ships with the package; use your IDE or `dotnet doc` tools until we publish DocFX output.

## Public API policy

There is no analyzer-enforced baseline (the `Microsoft.CodeAnalysis.PublicApiAnalyzers` gate was removed 2026-06 — see [Public API baselines](reference/PUBLIC_API_BASELINES.md)). Instead:

- Public-surface changes are caught in code review and recorded in `CHANGELOG.md`.
- Consumer plugins compile Common from a pinned source submodule, so removals or signature changes fail at plugin compile time on re-pin.
- Breaking changes (signature or behavior) must bump the `apiVersion` major and ship with clear migration guidance.

## Contract

- The host loads `Lidarr.Plugin.Abstractions` once into the default AssemblyLoadContext.
- Only types defined in this package may cross host ⇄ plugin boundaries.
- Public-surface changes must be intentional, reviewed, and recorded in `CHANGELOG.md`.
- Plugin manifests (`apiVersion`) reference the major version of this package.
- The Common library must treat Abstractions as an external dependency (no strong naming, no direct internals).
