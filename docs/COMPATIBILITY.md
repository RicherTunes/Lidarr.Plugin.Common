# Compatibility Matrix

Keep `Lidarr.Plugin.Common`, `Lidarr.Plugin.Abstractions`, and the host aligned so plugins load safely across versions.

## Supported combinations

| Host (Lidarr) | Abstractions | Common | TFMs | Status | Notes |
| --- | --- | --- | --- | --- | --- |
| 2.14.x | 1.x | 1.2.2 | net8.0, net6.0 | Active | Current release train. |
| 2.13.x | 1.x | 1.1.3 | net6.0 | Maintenance | Security fixes only; no new features. |
| 2.12.x | 0.x | 1.0.x | net6.0 | Deprecated (EOL 2025-12-31) | Schedule upgrade to 1.x ABI. |

### Versioning policy

- **Abstractions** – Semantic Versioning. Major bumps denote ABI changes; minors are additive; patch releases fix bugs without signature changes.
- **Common** – Plugin-private surface. Breaking changes may ship in any major version (plugins opt in). Multiple Common versions can coexist via ALC isolation.
- **NuGet packages** – Keep `AssemblyVersion` fixed within a major release to avoid binding redirects.

### Target frameworks

- `net8.0` is the preferred target for new plugins.
- `net6.0` remains supported until **31 March 2026** (after that, drop the compat build).
- Plugin manifests should list supported TFMs via `targets` for diagnostics.

## Contract

- Host upgrades that break the ABI must ship new `Lidarr.Plugin.Abstractions` majors and update `apiVersion` guidance.
- Common releases must remain compatible with all supported Abstractions majors (no shared static state across plugins).
- CI must run both TFMs (`dotnet test -f net8.0` + `net6.0`) until net6 is retired.
- The compatibility matrix must be updated alongside every release so plugin authors know when to migrate.
