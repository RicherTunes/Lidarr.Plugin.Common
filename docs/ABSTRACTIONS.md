# Lidarr.Plugin.Abstractions

`Lidarr.Plugin.Abstractions` is the host-owned Application Binary Interface (ABI). It defines the contracts that cross AssemblyLoadContexts and must remain stable across plugin updates.

## Core interfaces

- **`IPlugin`** – lifecycle entry point (`InitializeAsync`, factories for indexers/download clients, disposal).
- **`IPluginContext`** – host services (versions, logging, clock) supplied during plugin initialization.
- **`IIndexer`** – exposes search operations the host calls to discover releases.
- **`IDownloadClient`** – hands back download handles or streams to the host orchestrator.
- **`ISettingsProvider`** – type-safe access to plugin settings supplied by the host UI.

XML documentation for each interface ships with the package; use your IDE or `dotnet doc` tools until we publish DocFX output.

## Public API enforcement

Per-target-framework baselines (`PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`) live under `src/Abstractions/PublicAPI/<tfm>/`. A custom MSBuild target copies them into `obj/` before every compile so `Microsoft.CodeAnalysis.PublicApiAnalyzers` can verify:

- No public symbol is removed or renamed without updating the baseline.
- Additive changes require updating the `Unshipped` file.
- Breaking changes (signature or behavior) must bump the `apiVersion` major and ship with clear migration guidance.

## Contract

- The host loads `Lidarr.Plugin.Abstractions` once into the default AssemblyLoadContext.
- Only types defined in this package may cross host ⇄ plugin boundaries.
- Public API baselines are enforced per TFM; changes must be intentional and reviewed.
- Plugin manifests (`apiVersion`) reference the major version of this package.
- The Common library must treat Abstractions as an external dependency (no strong naming, no direct internals).
