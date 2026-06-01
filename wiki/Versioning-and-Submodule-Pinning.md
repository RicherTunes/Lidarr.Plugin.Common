# Versioning and Submodule Pinning

How Common releases are versioned, how plugin repos pin the submodule, and how the ecosystem stays in lockstep.

## Common version contract

Every plugin's `plugin.json` declares a `commonVersion` field that must match the `<Version>` in [src/Lidarr.Plugin.Common.csproj](../src/Lidarr.Plugin.Common.csproj). A parity-lint spec ([scripts/parity-spec.json](../scripts/parity-spec.json)) encodes the invariants, and `ecosystem-parity-lint.ps1` enforces them in CI.

Full details → [Ecosystem Version Contract](../docs/ECOSYSTEM_VERSION_CONTRACT.md).

## Submodule pin and sentinel file

Plugin repos consume Common as a git submodule under `ext/`. Alongside the submodule pointer, each plugin keeps an `ext-common-sha.txt` sentinel file whose content must match the submodule's checked-out SHA. CI drift detection reads this file and fails if it diverges.

The two scripts that automate repinning:

- [scripts/repin-common-submodule.ps1](../scripts/repin-common-submodule.ps1) — PowerShell (update SHA, sentinel, optionally stage).
- [scripts/repin-common-submodule.sh](../scripts/repin-common-submodule.sh) — Bash equivalent for CI workflows.

## Host-version upgrade flow

When the Lidarr host target changes, a structured playbook coordinates the bump across Common and every plugin repo.

See → [Host Version Upgrade Playbook](../docs/HOST_VERSION_UPGRADE_PLAYBOOK.md).

## Compatibility matrix

Supported host/abstractions/Common version combinations and target-framework policy.

See → [Compatibility](../docs/COMPATIBILITY.md).

## Upgrading Common in your plugin

Step-by-step checklist to follow after each tagged release.

See → [Upgrading](../docs/UPGRADING.md).
