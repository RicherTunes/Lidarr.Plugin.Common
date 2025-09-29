# Plugin Manifest (`plugin.json`) Reference

The host reads `plugin.json` from each plugin folder before loading its AssemblyLoadContext. This document captures every field, accepted values, and validation rules used by `PluginManifest`.

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| `id` | string | yes | Stable identifier (lowercase recommended). Used as the registry key inside the host. |
| `name` | string | yes | Human-friendly display name. |
| `version` | string | yes | Semantic version of the plugin package. Parsed with pre-release/build metadata ignored for compatibility checks. |
| `apiVersion` | string | yes | Required Abstractions major in `major.x` format (e.g., `1.x`). Must match the host-provided `Lidarr.Plugin.Abstractions` major. |
| `commonVersion` | string | no | Version of `Lidarr.Plugin.Common` bundled with the plugin. Informational only. |
| `minHostVersion` | string | no | Minimum Lidarr host version. If supplied, the host must be `>=` this SemVer. |
| `entryAssembly` | string | no | DLL that contains the plugin entry point (defaults to `{id}.dll`). |
| `capabilities` | string[] | no | Optional capability flags for diagnostics or UI. |
| `requiredSettings` | string[] | no | List of settings keys required by the plugin. |
| `description` | string | no | Optional longer description. |
| `author` | string | no | Optional attribution. |

## Validation rules (enforced in `PluginManifest`) 
- All required fields must be non-empty.
- `version` and `minHostVersion` must parse as SemVer; pre-release and build metadata are ignored when normalising.
- `apiVersion` must follow `major.x` format. Only the major is compared.
- Compatibility checks fail when:
  - `minHostVersion` > host version.
  - `apiVersion` major ≠ host Abstractions major.

See `tests/PluginManifestTests.cs` for concrete unit tests covering each validation case and `tests/PluginLoaderEdgeTests.cs` for host-side behaviour when manifests are missing or invalid.
