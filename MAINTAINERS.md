# Maintainers Guide

This file documents the manual release flow and day-to-day guardrails for this repo.

## Branch/PR Rules
- main is protected: PR required, strict status checks, linear history, resolved conversations. Only squash merges allowed; auto-merge disabled.
- Required checks: CI (Ubuntu + Windows), CodeQL, PR Validation.

## Versioning
- Source of truth: `<Version>` in each csproj (Common and Abstractions). Assembly attributes are SDK-generated.
- Tag format: `vX.Y.Z` must match `<Version>` (release workflow enforces).

## Manual Release Checklist
- Ensure CI green on main; resolve any docs lint or snippet issues.
- Update CHANGELOG.md and README “Latest” with the release version/date.
- Bump `<Version>` in:
  - `src/Lidarr.Plugin.Common.csproj`
  - `src/Abstractions/Lidarr.Plugin.Abstractions.csproj`
- (If public API surface changed) run `tools/Update-PublicApiBaselines.ps1` and commit.
- Open PR and squash-merge.
- Set repo secret `NUGET_API_KEY` (if publishing to NuGet.org).
- Tag: `git tag vX.Y.Z && git push origin vX.Y.Z`.
  - Release workflow will pack Common/Abstractions and publish to NuGet if the key is set.
  - Template publish workflow will pack the template and publish if the key is set.
- Verify:
  - NuGet packages appear on nuget.org.
  - README NuGet badges reflect the new version.
  - GitHub Release is created (if you use releases) and includes artifacts (SBOM if enabled).

## Security and Hygiene
- Secret scanning and push protection are enabled at the repo level.
- CodeQL runs on PRs and main; keep it green.
- Gitleaks runs on PRs and history; if it flags false positives, adjust `.gitleaks.toml` with precise file/line allowlists.

## When to Update Docs Gates
- Docs workflow is blocking. If you need to land structural changes, temporarily mark specific steps non-blocking, then re-tighten in the next PR.

## Template Package
- The `templates/Lidarr.Plugin.Templates.csproj` wraps `templates/lidarr-plugin/` for `dotnet new`.
- Published automatically on release tags if `NUGET_API_KEY` is configured; otherwise it’s skipped.
