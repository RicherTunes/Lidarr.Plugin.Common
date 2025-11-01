# Plugin Manifest

Each plugin ships a `plugin.json` that describes identity, compatibility expectations, and diagnostics. Treat it as a contract between the host and the plugin loader.

## Fields & rules

| Field | Type | Required | Example | Rule |
| --- | --- | --- | --- | --- |
| `id` | string | ✅ | `tidalarr` | Lowercase `[a-z0-9-]+`. Stable across releases. |
| `name` | string | ✅ | `Tidalarr` | 1–164 characters. Display only. |
| `version` | SemVer | ✅ | `2.3.1` | Plugin package version. |
| `apiVersion` | pattern | ✅ | `1.x` | Must match the major version of `Lidarr.Plugin.Abstractions` (`^\d+\.x$`). |
| `minHostVersion` | SemVer | ✅ | `2.14.0` | Host must be ≥ this version or the loader refuses the plugin. |
| `targets` | array | ➖ | `["net8.0"]` | Optional diagnostics for supported TFMs. |
| `commonVersion` | SemVer | ➖ | `1.2.2` | Informational only (per-plugin Common build). |

`additionalProperties` are not permitted; keep the manifest concise and explicit.

## Minimal manifest

```json
{
  "$schema": "./reference/plugin.schema.json",
  "id": "tidalarr",
  "name": "Tidalarr",
  "version": "2.3.1",
  "apiVersion": "1.x",
  "minHostVersion": "2.14.0"
}
```

## Full manifest example

```json
{
  "$schema": "./reference/plugin.schema.json",
  "id": "qobuzarr",
  "name": "Qobuzarr",
  "version": "2.5.0",
  "apiVersion": "1.x",
  "minHostVersion": "2.14.0",
  "targets": [
    "net8.0",
    "net6.0"
  ],
  "commonVersion": "1.2.2"
}
```

## CI validation

Use `tools/ManifestCheck.ps1` to guarantee the manifest matches the project file during builds.

```powershell file=../tools/ManifestCheck.ps1#manifest-ci
```

Run it in CI (see `.github/workflows/docs.yml`) or locally:

```powershell
pwsh tools/ManifestCheck.ps1 -ProjectPath plugins/Tidalarr/Tidalarr.csproj -ManifestPath plugins/Tidalarr/plugin.json
```

## Contract

- The loader rejects the plugin when `apiVersion` does not match the host ABI major version.
- `minHostVersion` guards against running on older hosts; loaders must fail fast with a helpful message.
- `commonVersion` is informational; different plugins may ship different Common builds side-by-side.
- Manifests must validate against `docs/reference/plugin.schema.json` (no additional properties, explicit fields only).
- Keep manifests under source control; never generate them at build time.
