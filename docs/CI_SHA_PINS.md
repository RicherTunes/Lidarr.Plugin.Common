# CI SHA Pins Inventory

**Generated**: 2026-05-23  
**Agent**: ci-cd-agent (Phase 1.5)  
**Scope**: All 5 ecosystem repos — brainarr, qobuzarr, tidalarr, applemusicarr, lidarr.plugin.common

---

## Summary

| Metric | Count |
|--------|-------|
| Total `uses:` action references scanned | 391 |
| SHA40-pinned (fully safe) | 3 |
| Major-version tag (e.g. `@v4`) | 381 |
| Semver tag (e.g. `@v2.8.0`) | 5 |
| Other / unpinned | 2 |
| **Unpinned third-party actions (priority)** | **42** |

> **Note**: GitHub-owned actions (`actions/*`) and `github/codeql-action/*` are
> tracked by GitHub's own signing infrastructure. Major-version tags (`@v4`) are
> acceptable for these first-party actions per current policy. Third-party actions
> (all other owners) SHOULD be pinned to a 40-char SHA for supply-chain safety.

---

## Third-Party Unpinned Actions (Priority — Action Required)

These are non-`actions/*` / non-`github/*` actions not pinned to a commit SHA.
Each should be bumped to a SHA pin in a dedicated PR.

| Workflow file | Line | Action | Current ref | Pin type | Recommendation |
|---------------|------|--------|-------------|----------|----------------|
| brainarr/.github/workflows/release.yml | 285 | `anchore/sbom-action` | `v0` | major-tag | Pin to SHA of `v0` latest |
| qobuzarr/.github/workflows/release.yml | 112 | `anchore/sbom-action` | `v0` | major-tag | Pin to SHA of `v0` latest |
| tidalarr/.github/workflows/release.yml | 113 | `anchore/sbom-action` | `v0` | major-tag | Pin to SHA of `v0` latest |
| lidarr.plugin.common/.github/workflows/release.yml | 182 | `anchore/sbom-action` | `v0` | major-tag | Pin to SHA of `v0` latest |
| lidarr.plugin.common/.github/workflows/akv-dp-check.yml | 31 | `azure/login` | `v3` | major-tag | Pin to SHA of `v3` latest |
| applemusicarr/.github/workflows/ci.yml | 160 | `codecov/codecov-action` | `v5` | major-tag | Pin to SHA of `v5` latest |
| applemusicarr/.github/workflows/ci.yml | 272 | `codecov/codecov-action` | `v5` | major-tag | Pin to SHA of `v5` latest |
| brainarr/.github/workflows/ci.yml | 600 | `codecov/codecov-action` | `v4` | major-tag | Pin to SHA of `v4` latest |
| applemusicarr/.github/workflows/ci.yml | 144 | `danielpalme/ReportGenerator-GitHub-Action` | `v5` | major-tag | Pin to SHA |
| applemusicarr/.github/workflows/ci.yml | 256 | `danielpalme/ReportGenerator-GitHub-Action` | `v5` | major-tag | Pin to SHA |
| applemusicarr/.github/workflows/docs.yml | 30 | `DavidAnson/markdownlint-cli2-action` | `v22` | major-tag | Pin to SHA |
| lidarr.plugin.common/.github/workflows/docs.yml | 43 | `errata-ai/vale-action` | `v2` | major-tag | Pin to SHA |
| applemusicarr/.github/workflows/gitleaks.yml | 21 | `gitleaks/gitleaks-action` | `v2` | major-tag | Pin to SHA |
| brainarr/.github/workflows/gitleaks.yml | 20 | `gitleaks/gitleaks-action` | `v2` | major-tag | Pin to SHA |
| lidarr.plugin.common/.github/workflows/gitleaks-history.yml | 17 | `gitleaks/gitleaks-action` | `v2` | major-tag | Pin to SHA |
| lidarr.plugin.common/.github/workflows/gitleaks.yml | 20 | `gitleaks/gitleaks-action` | `v2` | major-tag | Pin to SHA |
| qobuzarr/.github/workflows/ci.yml | 149 | `gitleaks/gitleaks-action` | `v2` | major-tag | Pin to SHA |
| qobuzarr/.github/workflows/security.yml | 36 | `gitleaks/gitleaks-action` | `v2` | major-tag | Pin to SHA |
| tidalarr/.github/workflows/gitleaks.yml | 21 | `gitleaks/gitleaks-action` | `v2` | major-tag | Pin to SHA |
| tidalarr/.github/workflows/security.yml | 36 | `gitleaks/gitleaks-action` | `v2` | major-tag | Pin to SHA |
| applemusicarr/.github/workflows/docs.yml | 42 | `lycheeverse/lychee-action` | `v2` | major-tag | Pin to SHA |
| brainarr/.github/workflows/docs-truth-check.yml | 18 | `lycheeverse/lychee-action` | `v2.8.0` | semver-tag | Pin to SHA |
| brainarr/.github/workflows/link-check.yml | 23 | `lycheeverse/lychee-action` | `v2.8.0` | semver-tag | Pin to SHA |
| lidarr.plugin.common/.github/workflows/docs.yml | 54 | `lycheeverse/lychee-action` | `v2` | major-tag | Pin to SHA |
| qobuzarr/.github/workflows/docs.yml | 46 | `lycheeverse/lychee-action` | `v2` | major-tag | Pin to SHA |
| lidarr.plugin.common/.github/workflows/scorecard.yml | 26 | `ossf/scorecard-action` | `v2.4.3` | semver-tag | Pin to SHA |
| brainarr/.github/workflows/dependency-update.yml | 122 | `peter-evans/create-pull-request` | `v8` | major-tag | Pin to SHA |
| brainarr/.github/workflows/digest-drift.yml | 76 | `peter-evans/create-pull-request` | `v8` | major-tag | Pin to SHA |
| lidarr.plugin.common/.github/workflows/release-drafter.yml | 21 | `release-drafter/release-drafter` | `v7` | major-tag | Pin to SHA |
| brainarr/.github/workflows/actionlint.yml | 18 | `reviewdog/action-actionlint` | `v1` | major-tag | Pin to SHA |
| brainarr/.github/workflows/release.yml | 293 | `sigstore/cosign-installer` | `v3` | major-tag | Pin to SHA |
| lidarr.plugin.common/.github/workflows/release.yml | 202 | `sigstore/cosign-installer` | `v3` | major-tag | Pin to SHA |
| qobuzarr/.github/workflows/release.yml | 119 | `sigstore/cosign-installer` | `v3` | major-tag | Pin to SHA |
| tidalarr/.github/workflows/release.yml | 120 | `sigstore/cosign-installer` | `v3` | major-tag | Pin to SHA |
| applemusicarr/.github/workflows/release.yml | 34 | `softprops/action-gh-release` | `v2` | major-tag | Pin to SHA |
| brainarr/.github/workflows/plugin-package.yml | 332 | `softprops/action-gh-release` | `v2` | major-tag | Pin to SHA |
| brainarr/.github/workflows/release.yml | 335 | `softprops/action-gh-release` | `v2` | major-tag | Pin to SHA |
| lidarr.plugin.common/.github/workflows/release.yml | 229 | `softprops/action-gh-release` | `v1` | major-tag | Pin to SHA |
| qobuzarr/.github/workflows/release.yml | 158 | `softprops/action-gh-release` | `v1` | major-tag | Pin to SHA |
| tidalarr/.github/workflows/release.yml | 159 | `softprops/action-gh-release` | `v2` | major-tag | Pin to SHA |
| lidarr.plugin.common/.github/workflows/docs.yml | 36 | `streetsidesoftware/cspell-action` | `v8` | major-tag | Pin to SHA |
| brainarr/.github/workflows/docs-lint.yml | 26 | `tj-actions/changed-files` | `v44` | major-tag | Pin to SHA |

---

## First-Party Actions Using Major-Version Tags (Acceptable)

The following `actions/*` and `github/codeql-action/*` actions use major-version tags.
This is acceptable per current policy (GitHub signs these and tracks tag provenance),
but SHA-pinning is strongly recommended for all production-critical jobs.

**Unique first-party action refs across all repos:**

| Action | Refs in use |
|--------|-------------|
| `actions/cache` | `@v4`, `@v5` |
| `actions/checkout` | `@v4`, `@v6` |
| `actions/dependency-review-action` | `@v4` |
| `actions/download-artifact` | `@v8` |
| `actions/github-script` | `@v7`, `@v8` |
| `actions/setup-dotnet` | `@v4`, `@v5` |
| `actions/setup-node` | `@v4`, `@v5`, `@v6` |
| `actions/setup-python` | `@v6` |
| `actions/upload-artifact` | `@v4`, `@v7` |
| `github/codeql-action/analyze` | `@v3`, `@v4` |
| `github/codeql-action/init` | `@v3`, `@v4` |
| `github/codeql-action/upload-sarif` | `@v4` |

> **Version drift notice**: Multiple major versions are in use simultaneously
> (e.g. `actions/checkout@v4` vs `@v6`, `actions/setup-dotnet@v4` vs `@v5`).
> Standardisation to the latest major version should be done in a follow-up PR.

---

## Already SHA-Pinned (Good Examples)

| Workflow file | Line | Action | SHA |
|---------------|------|--------|-----|
| tidalarr/.github/workflows/ci.yml | 70 | `actions/checkout` | `08eba0b27e820071cde6df949e0beb9ba4906955` |
| tidalarr/.github/workflows/ci.yml | 99 | `actions/setup-dotnet` | `baa11fbfe1d6520db94683bd5c7a3818018e4309` |

---

## Recommended Next Steps

1. **Immediate**: Pin all third-party actions in the table above to 40-char SHAs.
   Use `bulk-update-workflow-pins.sh` in `lidarr.plugin.common/scripts/` if available,
   or use the `actions/dependabot` security update configuration.

2. **Short-term**: Standardise first-party action versions to latest major across all repos.

3. **Policy**: Add `lint-workflow-sha-pins.ps1` enforcement to the CI lint-enforcement
   workflow to block new unsha'd third-party action refs in PRs.
