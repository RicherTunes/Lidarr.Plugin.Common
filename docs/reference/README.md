# Reference Contracts

This directory contains machine-readable contracts that define stable interfaces.

## Files

| File | Purpose |
|------|---------|
| `plugin.schema.json` | JSON Schema for plugin manifest (`plugin.json`) |
| `e2e-run-manifest.schema.json` | JSON Schema for E2E test runner output |

## Schema Strictness Rules

### e2e-run-manifest.schema.json

The schema uses a "strict core, extensible edges" design:

| Component | `additionalProperties` | Implication |
|-----------|------------------------|-------------|
| Top-level | `true` | New top-level fields won't break validation |
| `runner`, `lidarr`, `summary`, `redaction` | `false` | **Schema update required** for new fields |
| `sources`, `diagnostics`, `results[].details` | `true` | Extensible without schema changes |

### Adding Fields to Strict Objects

If you add a new field to `runner`, `lidarr`, `summary`, or `redaction`:

1. Update `e2e-run-manifest.schema.json` with the new property
2. Update `scripts/tests/Test-ManifestSchema.ps1` if the field has validation requirements
3. Bump `schemaVersion` if the change is breaking (removing fields, changing types)
4. Run `Test-ManifestSchema.ps1` to verify

### Version Policy

- `schemaVersion: "1.2"` - Current version
- Minor bumps (1.2 → 1.3): Additive changes, new optional fields
- Major bumps (1.x → 2.0): Breaking changes, removed fields, type changes

## Validation

Manifests include a `$schema` field with a fetchable raw URL, pinned to the git SHA that produced the manifest:

```json
{
  "$schema": "https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common/<sha>/docs/reference/e2e-run-manifest.schema.json",
  "schemaVersion": "1.2",
  ...
}
```

**Why SHA-pinned?**
- **Fetchable**: Tools like AJV/VSCode can auto-fetch and validate
- **Immutable**: Old manifests point to the exact schema version they were built against
- **Forward-compatible**: Schema can evolve on `main` without breaking existing artifacts

If git SHA is unavailable, falls back to `main` branch.
