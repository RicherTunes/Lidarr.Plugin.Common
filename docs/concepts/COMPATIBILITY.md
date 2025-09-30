# Compatibility Matrix

Use this page to confirm which versions of Lidarr, the Abstractions package, and Common can run together.

## Supported combinations
| Host (Lidarr) | Abstractions (major) | Common (major) | TFMs shipped | Support status |
|---------------|----------------------|----------------|--------------|----------------|
| 2.14.x        | 1.x                  | 1.x            | net8.0 / net6.0 | Active |
| 2.13.x        | 1.x                  | 1.x            | net6.0 | Maintenance (EOL 2026-03-31) |
| < 2.13        | 0.x                  | 0.x            | net6.0 | Unsupported |

- **Abstractions** is host-owned. The host loads one copy in the default AssemblyLoadContext. Plugins must declare the required major via `plugin.json.apiVersion` (e.g., `1.x`).
- **Common** is plugin-owned. Each plugin chooses the Common version it ships. Side-by-side versions work because each plugin runs in its own collectible AssemblyLoadContext.

## Target frameworks
| Package | TFMs | Notes |
|---------|------|-------|
| Lidarr.Plugin.Abstractions | net6.0, net8.0 | `AssemblyVersion` fixed per major. |
| Lidarr.Plugin.Common | net6.0, net8.0 | Plugin-private; choose the TFM that matches your plugin target. |

Plan for SDK upgrades (`dotnet --list-sdks`) and make sure CI covers all TFMs you ship.

## End-of-life schedule
| Date | Action |
|------|--------|
| 2025-12-31 | Stop publishing net6.0 assets if host migrates entirely to net8.0. |
| 2026-03-31 | End maintenance support for Lidarr 2.13.x + Common 1.x on net6.0. |

## Compatibility checks performed by the loader
1. Parse `plugin.json` using `PluginManifest.Load`.
2. Reject plugins when:
   - `apiVersion` major ≠ host Abstractions major.
   - Host version `< minHostVersion`.
   - `entryAssembly` is missing.
3. Log helpful diagnostics (`InvalidOperationException` with message details).

## Upgrade guidance
- When Abstractions gains new APIs (minor release): update `plugin.json.commonVersion` and install the new package. No loader changes required.
- When Abstractions bumps major: host updates first, then plugin authors rebuild against the new package and update `apiVersion`/`minHostVersion`. Document the change in [`migration/BREAKING_CHANGES.md`](../migration/BREAKING_CHANGES.md).

## Related docs
- [Architecture](ARCHITECTURE.md)
- [Plugin isolation](PLUGIN_ISOLATION.md)
- [Manifest reference](../reference/MANIFEST.md)
- [Release policy](../dev-guide/RELEASE_POLICY.md)
