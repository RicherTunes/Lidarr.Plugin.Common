#!/bin/bash
#
# DEPRECATED: bulk plugin workflow pin mutation is no longer the maintenance
# model. The ecosystem is Gitea-primary and Common enforces exactly one guarded
# GitHub CI mirror per active plugin.
#
# This script intentionally fails closed so old runbooks cannot bulk-mutate
# plugin .github/workflows files without per-repo review.

set -euo pipefail

cat >&2 <<'MSG'
DEPRECATED: bulk plugin workflow pin mutation is no longer maintained.

Use these instead:
  - Plugin Common submodule/sentinel pinning:
      ext/Lidarr.Plugin.Common/scripts/repin-common-submodule.sh
  - Ecosystem CI contract:
      pwsh scripts/ci/verify-ecosystem-ci-contract.ps1 -EcosystemRoot <siblings> -CI
  - Common workflow action pin checks:
      pwsh scripts/tests/Test-LintWorkflowShaPins.ps1
  - Plugin GitHub mirror policy:
      pwsh scripts/ci/verify-ecosystem-ci-contract.ps1 -EcosystemRoot <siblings> -CI
MSG

exit 1
