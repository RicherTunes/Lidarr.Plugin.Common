# Breaking Changes

Track ABI-breaking updates here so plugin authors can plan migrations. Each entry should link to the relevant release notes and documentation updates.

| Version | Component | Breaking? | Description | Migration steps | Docs |
|---------|-----------|-----------|-------------|-----------------|------|
| 1.0.0 | E2E Sanitization | Yes | Centralized `e2e-sanitize.psm1` module replaces duplicate pattern definitions | Import new module; deprecated patterns in `e2e-diagnostics.psm1` will be removed in next major version | [e2e-sanitize.psm1](../../scripts/lib/e2e-sanitize.psm1) |
| 1.0.0 | E2E Manifest | No | Added `fullSha` field to `sources.*` block for reproducibility | Optional additive field; existing consumers unaffected (schema allows additionalProperties) | [e2e-json-output.psm1](../../scripts/lib/e2e-json-output.psm1) |

Guidelines:

- Update this table whenever `PublicAPI.Shipped.txt` removes or changes APIs.
- Cross-link to the specific release entry in `CHANGELOG.md`.
- Reference detailed instructions in [`migration/FROM_LEGACY.md`](FROM_LEGACY.md) or other how-to guides.
