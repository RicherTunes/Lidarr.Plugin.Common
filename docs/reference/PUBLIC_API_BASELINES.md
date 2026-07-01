# Public API Baselines (removed)

> **Status: removed 2026-06.** The `Microsoft.CodeAnalysis.PublicApiAnalyzers`
> gate and its checked-in `PublicAPI.*.txt` baselines no longer exist in this
> repository. This page is kept only so existing links resolve and to record
> why the gate was retired.

## What was removed

- The `Microsoft.CodeAnalysis.PublicApiAnalyzers` PackageReference (and the
  `RS0016`/`RS0017`/`RS0025`/`RS0026` rules it enforced) from
  `Lidarr.Plugin.Common.csproj` and `Lidarr.Plugin.Abstractions.csproj`.
- The checked-in baselines `src/PublicAPI/<tfm>/PublicAPI.{Shipped,Unshipped}.txt`
  and `src/Abstractions/PublicAPI/<tfm>/PublicAPI.{Shipped,Unshipped}.txt`.
- The `Update-PublicApiBaselines.ps1` helper and the
  `PreparePublicApiBaselines` / `VerifyPublicApiAdditionalFiles` MSBuild targets.

## Why

`Lidarr.Plugin.Common` is ILRepack-merged **with `Internalize=true`** into each
plugin's shipped DLL, so its public surface is not a consumed package boundary
the way a normal library's is. Maintaining a per-TFM checked-in API baseline
added review friction and frequent baseline churn without protecting a real
consumer contract. The genuinely host-facing surface
(`Lidarr.Plugin.Abstractions`) is small and changes rarely.

## What replaces it

- **Semantic versioning + `CHANGELOG.md`** record intentional public-surface
  changes (the [release policy](../dev-guide/RELEASE_POLICY.md) checklist
  enforces a CHANGELOG entry).
- **Packaging-closure validation** (`ValidatePackageClosure`, run by the merged
  build) keeps the shipped plugin DLL free of assembly references the Lidarr
  host does not provide — the property the merged build actually depends on.

## Related docs

- [Architecture](../concepts/ARCHITECTURE.md)
- [Release policy](../dev-guide/RELEASE_POLICY.md)
- [Migration: breaking changes](../migration/BREAKING_CHANGES.md)
