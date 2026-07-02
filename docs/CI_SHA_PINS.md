# CI SHA Pins Policy

**Updated**: 2026-07-01

The May 2026 cross-repo GitHub Actions pin inventory is retired. Plugin repos
are Gitea-primary and the active ecosystem contract enforces exactly one
guarded GitHub CI mirror per plugin.

The active scope is Common-owned CI policy plus each plugin's guarded
`.github/workflows/ci.yml` mirror. Common still owns GitHub-side
release/security workflows, and those workflows must keep third-party `uses:`
references pinned or intentionally exempted by policy.

## Current Plugin Rule

- Plugin repos run merge-gating CI in `.gitea/workflows/ci.yml`.
- Plugin repos carry exactly one guarded GitHub CI mirror at
  `.github/workflows/ci.yml`.
- `scripts/ci/ecosystem-repos.json` records `mirrorWorkflows: 1` for every
  active plugin.
- `scripts/ci/verify-ecosystem-ci-contract.ps1 -CI` fails if a plugin adds an
  undeclared GitHub workflow mirror, omits a mirror, drops the shared lint
  runner/pin/secret-scan/verify-local gates, or leaves any mirror job without
  `if: ${{ github.server_url == 'https://github.com' }}`.

## Current Common Rule

- Keep Common's `.github/workflows` files only where GitHub is the real
  execution surface, such as release/security automation.
- Prefer digest or 40-character SHA pins for third-party actions.
- If a tag pin is intentionally kept, document why in the workflow or the
  relevant security policy.
- Run `pwsh ./scripts/tests/Test-LintWorkflowShaPins.ps1` before changing
  Common workflow action references.

## Do Not Use

`scripts/bulk-update-workflow-pins.sh` is deprecated. It used to mutate plugin
`.github/workflows` files in bulk, but plugin mirror changes now need per-repo
review and the Common ecosystem contract rather than a broad text rewrite.
