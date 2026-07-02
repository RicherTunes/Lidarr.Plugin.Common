<!-- docval:ignore-script-refs: references plugin-local verify wrappers and CI scripts as part of the plugin repo contract -->

# CI Gates (Plugin Repos)

This ecosystem treats packaging and parity checks as **non-negotiable gates**. Active plugin repos are Gitea-primary and must keep exactly one guarded GitHub CI mirror at `.github/workflows/ci.yml`.

## Required plugin workflow shape

Do not add extra plugin-root GitHub workflows such as `packaging-gates.yml` or `submodule-pin.yml`. The Common ecosystem CI contract expects one mirror workflow per plugin and verifies that it contains the same merge-critical gates as Gitea:

- `secret-scan`: redacted `gitleaks detect`
- `lint`: `ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 -Mode ci`
- `verify`: Common submodule pin guard plus `scripts/verify-local.ps1`
- job-level `if: ${{ github.server_url == 'https://github.com' }}` on every GitHub mirror job
- no `continue-on-error`, `|| true`, fallback lint subsets, or shared-runner skip switches

The GitHub mirror may use GitHub-native setup actions where Gitea installs tools directly, but the behavioral gates must stay equivalent.

## Gitea lint job

```yaml
jobs:
  lint:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive
      - name: Shared plugin lint gates
        shell: pwsh
        run: ./ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 -RepoPath . -CommonRoot ext/Lidarr.Plugin.Common -Mode ci
```

## What the gate enforces

- **Date parsing policy**: no culture-unsafe parsing in production code
- **Sync-over-async policy**: no unallowlisted hot-path blocking
- **Test trait policy**: deterministic tests must not silently fall out of CI
- **Ecosystem parity**: structural and version-contract drift detection
- **Plugin contract tests**: repo-owned Pester guards invoked by the shared runner
- **Packaging** via `scripts/verify-local.ps1` and Common packaging helpers
- **Manifest validation** with entrypoint/package closure checks
- **Merged sidecar policy**: packages must not contain `Lidarr.Plugin.Common.dll` or `Lidarr.Plugin.Abstractions.dll` sidecars

## Local equivalents (pre-flight)

From a plugin repo root (with Common submodule available):

```powershell
# Fast policy gates
pwsh ext/Lidarr.Plugin.Common/scripts/ci/run-plugin-lint-gates.ps1 `
    -RepoPath . -CommonRoot ext/Lidarr.Plugin.Common -Mode ci

# Full build/package/test gate
pwsh scripts/verify-local.ps1
```

If you need to debug lower-level packaging checks explicitly:

```powershell
$common = "ext/Lidarr.Plugin.Common"

# Manifest validation (includes -ResolveEntryPoints)
& "$common/tools/ManifestCheck.ps1" -ProjectPath "src/Your.Plugin/Your.Plugin.csproj" -ManifestPath "src/Your.Plugin/plugin.json" -PublishPath "src/Your.Plugin/artifacts/publish/net8.0/Release" -ResolveEntryPoints

# Legacy sidecar check (passes when packages have no Abstractions sidecar)
& "$common/scripts/Verify-CanonicalAbstractions.ps1" -PackagePaths @("src/Your.Plugin/artifacts/packages/*.zip")
```

## Enforcement

Common's `scripts/ci/verify-ecosystem-ci-contract.ps1` enforces the one-mirror policy, shared-runner wiring, verify-local wiring, pin guard, secret scan, GitHub-only job guards, and failure-closed mirror behavior.
