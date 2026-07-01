#!/bin/bash
#
# DEPRECATED: plugin-root GitHub workflow mirrors were removed from the active
# plugin repos. The ecosystem is Gitea-primary and Common enforces
# mirrorWorkflows=0 for every active plugin.
#
# This script intentionally fails closed so old runbooks cannot recreate or
# mutate deleted plugin .github/workflows files.

set -euo pipefail

cat >&2 <<'MSG'
DEPRECATED: plugin-root GitHub workflow mirrors are no longer maintained.

Use these instead:
  - Plugin Common submodule/sentinel pinning:
      ext/Lidarr.Plugin.Common/scripts/repin-common-submodule.sh
  - Ecosystem CI contract:
      pwsh scripts/ci/verify-ecosystem-ci-contract.ps1 -EcosystemRoot <siblings> -CI
  - Common workflow action pin checks:
      pwsh scripts/tests/Test-LintWorkflowShaPins.ps1
MSG

exit 1
