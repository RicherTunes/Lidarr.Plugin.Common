# CI SHA Pins Policy

**Updated**: 2026-07-01

The May 2026 cross-repo GitHub Actions pin inventory is retired. Plugin repos
are now Gitea-primary and the active ecosystem contract enforces zero
plugin-root GitHub Actions workflows.

The active scope is `lidarr.plugin.common/.github/workflows`: Common still owns
GitHub-side release/security workflows, and those workflows must keep
third-party `uses:` references pinned or intentionally exempted by policy.

## Current Plugin Rule

- Plugin repos run merge-gating CI in `.gitea/workflows/ci.yml`.
- Plugin repos must not carry plugin-root `.github/workflows/*.yml` or
  `.github/workflows/*.yaml` files.
- `scripts/ci/ecosystem-repos.json` records `mirrorWorkflows: 0` for every
  active plugin.
- `scripts/ci/verify-ecosystem-ci-contract.ps1 -CI` fails if a plugin adds an
  undeclared GitHub workflow mirror.

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
`.github/workflows` files, but those mirrors no longer exist and must not be
recreated as part of Common promotion.
