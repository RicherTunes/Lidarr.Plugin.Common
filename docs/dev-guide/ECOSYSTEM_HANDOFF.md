# Ecosystem Handoff (Multi-Agent)

This file exists so another AI (or human) can pick up ecosystem parity work quickly without re-discovering context.

## Canonical References

- `docs/ECOSYSTEM_PARITY_ROADMAP.md` — current status + weeks-scale work queue.
- `docs/PERSISTENT_E2E_TESTING.md` — local E2E usage.
- `docs/E2E_ERROR_CODES.md` — error codes + structured details contract.
- `docs/PACKAGING.md` — packaging contract and forbidden DLL policy.

## Working Rules (Do Not Skip)

1. **Thin Common**: `lidarr.plugin.common/` owns shared primitives + guardrails, not provider-specific policy.
2. **Delete-or-don’t-add**: any Common addition must delete measurable duplication in ≥1 plugin within ≤2 follow-up PRs.
3. **TDD-first**: new contracts require hermetic tests and/or golden fixtures before wiring.
4. **E2E is acceptance**: for runtime-impacting changes, run `scripts/e2e-runner.ps1 -Gate bootstrap -EmitJson`.
5. **Avoid hot-file collisions**: claim file sets; avoid concurrent edits to:
   - `scripts/e2e-runner.ps1`
   - `scripts/lib/e2e-gates.psm1`
   - `build/PluginPackaging.targets`
   - `.github/workflows/*`

## How To Continue Work

### If you’re changing Common

1. Add tests first (`tests/` and/or `scripts/tests/`).
2. Keep PR scope tight and name the downstream deletion PR(s) that must follow.
3. After merge, bump submodules in:
   - `qobuzarr/ext/Lidarr.Plugin.Common`
   - `tidalarr/ext/Lidarr.Plugin.Common`
   - `brainarr/ext/lidarr.plugin.common`
   - `applemusicarr/ext/lidarr.plugin.common`

### If you’re changing a plugin repo

1. **Do not** patch Common by editing plugin `ext/` contents.
2. Prefer “pure bumps” for submodule updates (only submodule pointer + `ext-common-sha.txt`).
3. Run that repo’s `dotnet build` + targeted tests, then run ecosystem E2E when relevant.

## Helper Commands

```powershell
# Common (shared library)
dotnet test D:\Alex\github\lidarr.plugin.common\Lidarr.Plugin.Common.sln -c Release

# Ecosystem E2E (example)
cd D:\Alex\github\lidarr.plugin.common\scripts
.\e2e-runner.ps1 -Gate bootstrap -Plugins 'Qobuzarr,Tidalarr,Brainarr' -EmitJson
```

