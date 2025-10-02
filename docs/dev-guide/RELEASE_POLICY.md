# Release & Versioning Policy

This repository ships two NuGet packages:

1. `Lidarr.Plugin.Abstractions` – host-owned ABI (stable contract).
2. `Lidarr.Plugin.Common` – plugin-owned implementation.

## Abstractions (ABI)

- **Semantic Versioning** with conservative evolution.
- **AssemblyVersion** stays fixed for the entire major (1.0.0.0 for 1.x) to avoid binding redirect noise.
- **CI Guardrails**: `Microsoft.CodeAnalysis.PublicApiAnalyzers` + checked-in `PublicAPI.*` baselines force intentional API changes.
- **Breaking changes** require:
  - Major version bump.
  - Migration notes for plugin authors.
  - Host loader compatibility tests.

## Common (Implementation library)

- Also uses SemVer, but treated as plugin-private.
- Plugins choose which version to ship; side-by-side loading works because each plugin runs in its own AssemblyLoadContext.
- Breaking changes are acceptable with major bumps since consumers opt-in per plugin.

## Coordinating releases

1. **Author changes** in feature branches; keep CHANGELOG up to date (`CHANGELOG.md`).
2. **Update docs** relevant to the change (migrations, manifest schema, isolation guide).
3. **Run tests** (`dotnet test`). Isolation/manifest suites must pass.
4. **Tagging**:
   - Tag combined releases with `vX.Y.Z`. Reference the release template in [docs/UPGRADING.md](../UPGRADING.md).
5. **Publishing**: use `.github/workflows/release.yml`, which packs Abstractions and Common and runs API compatibility against the previous tag before pushing to NuGet.
6. **Communicate**:
   - Announce Abstractions bumps to plugin authors with the migration checklist (`../migration/FROM_LEGACY.md`).
   - For Common updates, highlight notable changes but remind authors they can upgrade at their own pace.

## Checklist before release

- [ ] CHANGELOG entry complete.
- [ ] Docs updated (`README`, playbooks, migration guides).
- [ ] Public API baselines (`src/Abstractions/PublicAPI.*`) refreshed when Abstractions changes.
- [ ] Tests green (`dotnet test`).
- [ ] Sample loader (`examples/IsolationHostSample`) still compiles and loads generated plugins.
- [ ] Packages pack locally (`dotnet pack`).

This process keeps the ABI stable for the host while allowing plugins and shared implementation code to evolve independently without assembly conflicts.
