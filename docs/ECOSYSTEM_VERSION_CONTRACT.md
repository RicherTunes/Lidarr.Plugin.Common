# Ecosystem Version Contract

## Why the contract exists

As the plugin ecosystem grew from a single shared helper into a library that four active plugins depend on simultaneously, version drift became a recurring source of build and runtime failures. Early coordination relied on informal conventions: each plugin author pinned the Common submodule manually, updated `plugin.json`'s `commonVersion` field by hand, and relied on code review to catch mismatches. In practice this meant that after every Common release at least one plugin would ship with a stale `commonVersion`, an outdated `targetFramework`, or a forbidden manifest field that the host silently ignored — until a new Lidarr build started enforcing it and broke loading.

A reviewer audit (completed May 2026) catalogued these coordination failures across all four plugin repos. The audit found repeated instances of:

- `commonVersion` in `plugin.json` lagging behind the submodule SHA by one or more releases.
- `manifest.json` and `plugin.json` disagreeing on the version field after a patch release.
- Forbidden fields (`minimumVersion`, `targets`) surviving in shipped manifests.
- Plugins targeting `net6.0` long after the ecosystem moved to `net8.0`.

The version contract is the machine-checkable answer to those problems. It encodes the invariants as a single JSON specification (`scripts/parity-spec.json`) and enforces them in CI via `ecosystem-parity-lint.ps1`.

## Schema: the `versionContract` section of `parity-spec.json`

The `versionContract` object is the authoritative source for ecosystem-wide version requirements. Each field is described below.

### `description`

Human-readable summary of the contract's purpose and the lint phase that enforces it.

### `commonVersionSource`

Path (relative to the Common repo root) of the `.csproj` that is the single source of truth for the canonical `commonVersion` value. The lint script reads the `<Version>` element from this file at runtime rather than hard-coding the version string, so the contract stays correct automatically whenever Common is released.

Current value: `src/Lidarr.Plugin.Common.csproj`

### `targetFramework`

The required target framework moniker for all shipping plugin assemblies.

Current value: `net8.0`

### `forbiddenTargetFrameworks`

Target framework monikers that must not appear in any plugin's `plugin.json`, `manifest.json`, or `.csproj`. Any occurrence causes a lint failure.

Current values: `net6.0`, `net7.0`, `netstandard2.0`, `netstandard2.1`

### `allowedHostAssemblies`

The exhaustive list of Lidarr host DLL names that a plugin is permitted to reference. Any assembly reference not on this list is treated as a packaging error (the plugin would be bundling a host-owned DLL). The parity lint checks packaged ZIPs for forbidden contents.

### `forbiddenPackageContents`

DLL names that must not appear in a plugin's packaged ZIP. These are either host-owned assemblies or framework assemblies that Lidarr provides at runtime. Bundling them causes `AssemblyLoadContext` type-identity collisions when multiple plugins are loaded simultaneously.

## How a plugin author validates compliance

Run the parity lint locally before opening a PR:

```powershell
pwsh ext/Lidarr.Plugin.Common/scripts/ecosystem-parity-lint.ps1 `
  -RepoPath . `
  -Mode ci `
  -Check VersionContract `
  -CommonRoot ext/Lidarr.Plugin.Common
```

To run all checks at once (recommended before a release):

```powershell
pwsh ext/Lidarr.Plugin.Common/scripts/ecosystem-parity-lint.ps1 `
  -RepoPath . `
  -Mode ci `
  -Check all `
  -CommonRoot ext/Lidarr.Plugin.Common
```

The script exits `0` on success and non-zero on any failure. CI treats a non-zero exit as a blocking gate.

## What the lint catches

The `VersionContract` check enforces the following invariants:

1. **`commonVersion` drift** — the `commonVersion` field in `plugin.json` must match the `<Version>` element in `src/Lidarr.Plugin.Common.csproj`. A plugin that pins the submodule to a newer Common release but does not update `commonVersion` will fail this check.

2. **`plugin.json` / `manifest.json` version parity** — the `version` field in `plugin.json` and the `version` field in `manifest.json` must be identical. Intra-repo drift (e.g., bumping `plugin.json` for a patch but forgetting `manifest.json`) is forbidden.

3. **Forbidden fields** — fields listed under `pluginJson.forbiddenFields` and `manifestJson.forbiddenFields` in `parity-spec.json` (currently `minimumVersion` and `targets`) must not be present in either file.

4. **Forbidden target frameworks** — `targetFramework` values that appear in `forbiddenTargetFrameworks` must not appear in either manifest file or in any `.csproj` inside the plugin's `src/` tree.

5. **Forbidden package contents** — when `-Check PackageContents` is combined, the lint inspects the built ZIP for DLLs that must not be bundled (host-owned and framework-owned assemblies).

## How to bump Common version and propagate to plugins

Today the propagation is a manual, repo-by-repo process. The steps are:

1. **In Common**: update `src/Lidarr.Plugin.Common.csproj` with the new `<Version>` value, update `CHANGELOG.md`, tag the release, and push.

2. **In each plugin repo** (brainarr, qobuzarr, tidalarr, applemusicarr):
   a. Run `git submodule update --remote ext/Lidarr.Plugin.Common` to advance the submodule pointer to the new Common tag.
   b. Update the `ext-common-sha.txt` file to the new commit SHA (used by `submodule-pin.yml`).
   c. Update the `commonVersion` field in `plugin.json` to match the new Common `<Version>`.
   d. Update the `version` field in `manifest.json` if the plugin version itself is also being bumped.
   e. Run `pwsh ext/Lidarr.Plugin.Common/scripts/ecosystem-parity-lint.ps1 -Check VersionContract -Mode ci` and confirm exit code `0`.
   f. Open a PR with the submodule bump and manifest updates.

Future automation (planned for the cross-cutting infrastructure layer) will add a GitHub Actions workflow that opens coordinated submodule-bump PRs across all plugin repos automatically when a new Common version is published. Until that workflow exists, the manual steps above are the required procedure.
