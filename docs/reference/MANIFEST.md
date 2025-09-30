# Manifest Reference (`plugin.json`)

The host inspects `plugin.json` before loading a plugin. This page is the single source of truth for every field, validation rule, and compatibility check.

## Fields
| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `id` | string | ✓ | Stable, lowercase identifier for the plugin. Used as a registry key. |
| `name` | string | ✓ | Human-friendly name. |
| `version` | string | ✓ | Semantic version of the plugin package. Build metadata is ignored for compatibility checks. |
| `apiVersion` | string (`major.x`) | ✓ | Abstractions major the plugin expects (e.g., `1.x`). Must match the host ABI major. |
| `commonVersion` | string | – | Version of `Lidarr.Plugin.Common` bundled with the plugin. Diagnostic only. |
| `minHostVersion` | string | – | Minimum host version. Loader rejects when host < this SemVer. |
| `minCommonVersion` | string | – | Optional hint for recommended Common version (host does not enforce). |
| `entryAssembly` | string | – | DLL name containing the plugin entry point. Defaults to `{id}.dll`. |
| `capabilities` | string[] | – | Display hints (e.g., `Search`, `Download`). |
| `requiredSettings` | string[] | – | Keys that must be configured. Helps the host pre-validate settings. |
| `description` | string | – | Longer summary shown in UIs. |
| `author` | string | – | Attribution. |

## Truth table
| Scenario | Loader outcome |
|----------|----------------|
| `apiVersion` major equals host Abstractions major | Load proceeds. |
| `apiVersion` major differs | Loader throws `InvalidOperationException` containing "abstractions major". |
| Host version < `minHostVersion` | Loader throws `InvalidOperationException` containing "Host version". |
| `entryAssembly` missing on disk | Loader throws `FileNotFoundException`. |
| Required field missing/empty | `PluginManifest.Load` throws `InvalidOperationException` describing the field. |

## Example
```json
{
  "id": "myplugin",
  "name": "My Plugin",
  "version": "1.2.0",
  "apiVersion": "1.x",
  "commonVersion": "1.1.4",
  "minHostVersion": "2.12.0",
  "entryAssembly": "MyPlugin.dll",
  "capabilities": ["Search", "Download"],
  "requiredSettings": ["ClientId", "ClientSecret"],
  "description": "Search and download from My Service",
  "author": "Your Name"
}
```

## Validation rules
`PluginManifest` normalises SemVer values before comparison. It removes pre-release/build metadata and ensures missing patch numbers default to zero. Validation occurs in this order:
1. Ensure required fields are present (`id`, `name`, `version`, `apiVersion`).
2. Parse `version` and `minHostVersion` (if provided) as SemVer.
3. Validate `apiVersion` matches `major.x` format.
4. During `EvaluateCompatibility`:
   - Host version must satisfy `minHostVersion` (when supplied).
   - Host Abstractions major must equal `apiVersion` major.

See `tests/PluginManifestTests.cs` for unit coverage and `tests/PluginLoaderEdgeTests.cs` for integration-level guards.

## Author checklist
- [ ] Keep `id` immutable across releases.
- [ ] Update `commonVersion` to reflect the Common DLL shipped with your plugin.
- [ ] Populate `requiredSettings` so hosts can surface missing configuration early.
- [ ] Include `minHostVersion` when you rely on new host features.

## Related docs
- [Architecture](../concepts/ARCHITECTURE.md)
- [Compatibility matrix](../concepts/COMPATIBILITY.md)
- [Settings reference](SETTINGS.md)
- [Migration guides](../migration/FROM_LEGACY.md)
