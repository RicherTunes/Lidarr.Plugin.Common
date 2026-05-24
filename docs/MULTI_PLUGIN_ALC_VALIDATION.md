# Multi-Plugin ALC Coexistence Validation

This document explains what the Phase 2 test suites (`MultiPluginAlc` and
`PackageClosure`) validate, how to run them locally, and how they relate to the
Lidarr host's actual AssemblyLoadContext behaviour.

---

## What the ALC fix unblocked

For roughly six months, installing two or more RicherTunes plugins (brainarr,
qobuzarr, tidalarr, applemusicarr) into a single Lidarr instance silently failed.
Only the first plugin to load would appear in `/api/v1/{indexer,downloadclient}/schema`;
the others were swallowed by the CLR with a stream of:

```
[Warn] Bootstrap: Error starting with plugins enabled
Could not load file or assembly 'Lidarr.Plugin.Abstractions, Version=1.0.0.0, …
An operation is not legal in the current state. (0x80131509)
```

The full root-cause analysis is in `docs/dev-guide/ALC_MULTIPLUGIN_FIX.md`. In
short: every plugin shipped its own sidecar `Lidarr.Plugin.Abstractions.dll`. When
the host's plugin scanner loaded plugin A, it resolved Abstractions from plugin A's
directory into the **default** ALC. When it loaded plugin B, the runtime refused to
load the same assembly identity from a different path — `COR_E_INVALIDOPERATION`.

The fix was to ILRepack-merge `Lidarr.Plugin.Abstractions` into each plugin's merged
DLL so no sidecar is emitted. The merged plugin DLL contains a private copy of the
Abstractions types that the CLR treats as a distinct identity per ALC — no conflict.

These test suites are the automated gate that proves the fix holds and that no future
change to PluginPackaging.targets silently re-introduces the conflict.

---

## What the two test suites validate

### Suite A — `tests/MultiPluginAlc/MultiPluginAlcTests.cs`

| Test | What it proves |
|------|---------------|
| `LoadAllPluginsInIsolatedAlcs_NoTypeIdentityCollisions` | All 4 plugin DLLs can be loaded into 4 separate `PluginLoadContext` instances. The `IPlugin` type resolved from each ALC is a **different** runtime `Type` object — confirming genuine ALC isolation, not shared-ALC reuse. |
| `LoadAllPluginsInIsolatedAlcs_NoSharedDependencyConflict` | None of the 4 plugins' `.deps.json` files list `Lidarr.Plugin.Common` as an external dependency. Delegating Common resolution to the host is a misconfiguration (the host does not ship Common). |
| `LoadAllPluginsInIsolatedAlcs_AllPluginMetadataExposed` | Each plugin DLL exposes at least one concrete type implementing `Lidarr.Plugin.Abstractions.Contracts.IPlugin`. Uses name-based reflection so it works regardless of whether Abstractions is a sidecar or an ILRepack-merged internal. |
| `LoadAllPluginsInIsolatedAlcs_NoAssemblyVersionDrift` | Where a sidecar `Lidarr.Plugin.Common.dll` exists, its `AssemblyVersion` matches the canonical `1.8.0.0`. Drift here means the plugin was built against a different Common version than the ecosystem declares. |

The suite uses `[SkippableFact]` — individual plugins skip when their DLL is not
built locally rather than failing the run. CI is expected to always have all 4 built.

### Suite B — `tests/PackageClosure/PackageClosureTests.cs`

| Test | What it proves |
|------|---------------|
| `PluginPackage_ContainsNoForbiddenAssemblies` (Theory × 4) | None of the DLLs in the plugin's canonical build output directory match any entry in `scripts/parity-spec.json → versionContract.forbiddenPackageContents`. Finding one is a HIGH-SEVERITY violation. |
| `PluginPackage_ContainsRequiredFiles` (Theory × 4) | The build output contains the main plugin DLL and a valid `plugin.json` with required fields. |
| `PluginPackage_FullInventoryReport` (Theory × 4) | Emits a full classified inventory (OK / FORBIDDEN) to the test output. Always passes — intended as a CI log artefact for auditing package bloat. |

The forbidden-assembly list is loaded live from `scripts/parity-spec.json` at
test startup; the test falls back to a hardcoded copy if the spec file cannot be
found from the test execution directory.

---

## How to run locally before release

### Prerequisites

Build each plugin in Release mode before running the suite:

```powershell
dotnet build "C:\R\Alex\github\brainarr" --configuration Release
dotnet build "C:\R\Alex\github\qobuzarr" --configuration Release
dotnet build "C:\R\Alex\github\tidalarr" --configuration Release
dotnet build "C:\R\Alex\github\applemusicarr" --configuration Release
```

### Running just the Phase 2 tests

From the `lidarr.plugin.common` repository root:

```powershell
dotnet test tests/Lidarr.Plugin.Common.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~MultiPluginAlc|FullyQualifiedName~PackageClosure" `
  --logger "console;verbosity=detailed"
```

Expected outcomes:

- **GREEN**: plugin DLL found, all assertions pass.
- **SKIPPED**: plugin DLL not found locally (acceptable; emit a build reminder).
- **FAILED**: assertion violation — a forbidden DLL was found or a type-identity
  collision was detected.

### Running the full test suite

```powershell
dotnet test tests/Lidarr.Plugin.Common.Tests.csproj --configuration Release
```

The Phase 2 tests are part of the same project as all other Common tests; they run
alongside Phase 0 Pester tests and Phase 1 unit tests in a single `dotnet test`
invocation.

---

## Relationship to the Lidarr host's ALC behaviour

The Lidarr host (as of 2.14.x) discovers plugins by:

1. Scanning `/config/plugins/RicherTunes/<PluginName>/` for a `plugin.json`.
2. Reading the `main` field to locate the plugin DLL.
3. Creating an `AssemblyLoadContext` for the plugin.
4. Reflecting on all types in the DLL to find classes that derive from
   `HttpIndexerBase` / `DownloadClientBase` / `ImportListBase` (all defined in
   `Lidarr.Core.dll`).

The host does **not** reference `Lidarr.Plugin.Abstractions` at all — it defines
its own `NzbDrone.Core.Plugins.IPlugin` which is entirely separate. Abstractions
is only used by our own tooling (PluginSandbox, CLI, test helpers).

The `PluginLoadContext` in `src/Abstractions/Hosting/PluginLoadContext.cs` is the
same ALC implementation the host uses: it delegates shared contract assemblies
(MEL, DI abstractions) back to the default ALC and resolves all plugin-private
dependencies from the plugin's own directory.

The type-identity isolation guarantee tested by Suite A is precisely the property
that the post-fix packaging needs to maintain: each plugin's private Abstractions
types live in their own ALC and are never shared across ALCs — preventing the
`COR_E_INVALIDOPERATION` load failure.

For the complete narrative, see `docs/dev-guide/ALC_MULTIPLUGIN_FIX.md`.

---

## Forbidden assembly policy and parity-spec

`scripts/parity-spec.json → versionContract.forbiddenPackageContents` is the single
source of truth for DLLs that must never appear in a plugin package. The current list
is:

| Assembly | Reason |
|----------|--------|
| `FluentValidation.dll` | Shipped by the Lidarr host — version mismatch causes silent method-signature failures |
| `NLog.dll` | Host-provided logger; second copy causes duplicate log entries and potential NREs |
| `System.Text.Json.dll` | Host BCL assembly; version mismatch breaks `JsonSerializer` calls |
| `Microsoft.Extensions.*` (several) | Host framework assemblies — version drift causes load failures |
| `Lidarr.Core.dll` / `Lidarr.Common.dll` / `Lidarr.Http.dll` / `Lidarr.Api.V1.dll` | Core host assemblies; shipping them is always wrong |
| `NzbDrone.Core.dll` / `NzbDrone.Common.dll` | Legacy host names for the same assemblies |

Suite B (`PackageClosureTests`) enforces this policy automatically on every CI run.

### Note for Phase 3 work

At time of writing (2026-05-23), inspection of `tools/PluginPack.psm1` and
`build/PluginPackaging.targets` shows that the **packaging scripts do not actively
strip** forbidden assemblies at build time — they rely on the fact that the
`_PluginRuntimeDeps` `ItemGroup` simply does not include them. If a future dependency
change causes one of these assemblies to be added transitively (e.g., via
`CentralPackageTransitivePinningEnabled=true` picking up a new version), the
packaging script would include it silently.

Recommended Phase 3 hardening: add an `<Error Condition="..." />` gate in
`PluginPackaging.targets` that fails the build when any forbidden assembly name
appears in `_PluginRuntimeDeps`. This converts a post-hoc test failure into a
pre-commit build failure.
