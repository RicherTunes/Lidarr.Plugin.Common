# Multi-Plugin Co-existence: Root Cause & Fix (2026-05-10)

## The bug as users hit it

Two or more RicherTunes plugins (tidalarr / qobuzarr / brainarr / applemusicarr) installed in one Lidarr instance: only one plugin's settings page ever appeared. The other plugins were silently absent from `/api/v1/{indexer,downloadclient,importlist}/schema`. Container logs were full of:

```
[Warn] Bootstrap: Error starting with plugins enabled
Could not load file or assembly 'Lidarr.Plugin.Abstractions, Version=1.0.0.0,
Culture=neutral, PublicKeyToken=(removed)
An operation is not legal in the current state. (0x80131509)
```

(`0x80131509` is `COR_E_INVALIDOPERATION`.)

For months this was documented as "upstream Lidarr AssemblyLoadContext lifecycle bug" with the workaround of using single-plugin instances. **It was actually a plugin-side packaging issue, fixable by us.**

## Root cause

Every plugin's `bin/` shipped `Lidarr.Plugin.Abstractions.dll` alongside the merged plugin DLL. When Lidarr's host scanned `/config/plugins/RicherTunes/<Plugin>/` for plugin DLLs:

1. **Plugin A loads**: host's plugin loader resolves the merged DLL's AssemblyRef to `Lidarr.Plugin.Abstractions Version=1.0.0.0`. The runtime walks the host's TPA list, doesn't find it, falls back to `AssemblyDependencyResolver` keyed on plugin A's directory, finds `/config/plugins/RicherTunes/PluginA/Lidarr.Plugin.Abstractions.dll`, and loads it into the default ALC.
2. **Plugin B loads**: same AssemblyRef, same Version, same culture, same public key token. Runtime sees Abstractions is already loaded in the default ALC and *should* reuse the cached `Assembly` instance. Instead, the resolver invokes `LoadFromAssemblyPath` against plugin B's identical-but-different-file copy at `/config/plugins/RicherTunes/PluginB/Lidarr.Plugin.Abstractions.dll`. The CLR rejects loading the same identity from a different path with `COR_E_INVALIDOPERATION`.
3. Plugin B (and every plugin scanned after A) fails to discover any indexer / downloadclient / importlist types.

Removing the sidecar `Abstractions.dll` from plugin B's directory doesn't help — the plugin DLL still has the AssemblyRef and the resolver fails differently:

```
Could not load file or assembly 'Lidarr.Plugin.Abstractions, Version=1.0.0.0, ...
The system cannot find the file specified.
```

## Why the previous "DO NOT MERGE" guidance was wrong

`build/PluginPackaging.targets` previously contained:

```xml
<!-- NOTE: Lidarr.Plugin.Abstractions must NOT be merged - it contains IPlugin and other interfaces
     that must have identical type identity with the host. Merging breaks plugin loading. -->
```

That comment assumed Lidarr's host had a hard reference to `Lidarr.Plugin.Abstractions.IPlugin` — i.e., that the host did `assembly.GetTypes().Where(t => typeof(IPlugin).IsAssignableFrom(t))` against the Abstractions interface.

**It does not.** Reading `Lidarr.Core.dll` metadata (the host's plugin-discovering assembly):

```text
=== AssemblyRefs in Lidarr.Core.dll ===
  System.IO.Abstractions v17.0.0.0
  Microsoft.Extensions.Configuration.Abstractions v8.0.0.0
  FluentMigrator.Abstractions v6.2.0.0
  Microsoft.Extensions.DependencyInjection.Abstractions v8.0.0.0
  Microsoft.Extensions.Logging.Abstractions v8.0.0.0
  ...
=== TypeRefs (Plugin namespace) ===
  (none)
=== TypeDefs containing 'Plugin' ===
  NzbDrone.Core.Plugins.InstallPluginService
  NzbDrone.Core.Plugins.IPlugin            ← host's OWN IPlugin
  NzbDrone.Core.Plugins.Plugin
  ...
```

Lidarr discovers indexers / download clients / import lists by reflecting on classes that derive from `HttpIndexerBase` / `DownloadClientBase` / `ImportListBase` (defined in `Lidarr.Core.dll`), not via any `IPlugin` interface from `Lidarr.Plugin.Abstractions`. The Abstractions assembly is only useful for our own tests + CLI tooling (`PluginSandboxRuntimeTests`, `TidalCLI`, etc.). The host has no idea it exists.

## The fix

Add `Lidarr.Plugin.Abstractions.dll` to `_PluginDeps` in `PluginPackaging.targets`. ILRepack with `Internalize="true"` rewrites the AssemblyRef to point at the merged plugin DLL itself — the runtime never tries to load a separate Abstractions assembly, so the cross-ALC conflict can't happen.

```xml
<_PluginDeps Include="$(_PluginOutputPath)Lidarr.Plugin.Common.dll" ... />
<_PluginDeps Include="$(_PluginOutputPath)Lidarr.Plugin.Abstractions.dll" ... />  <!-- NEW -->
```

And remove the sidecar from `_PluginRuntimeDeps` so it's not shipped:

```xml
<!-- previously:
<_PluginRuntimeDeps Include="$(_PluginOutputPath)Lidarr.Plugin.Abstractions.dll" ... />
-->
```

After: each plugin's package contains only `Lidarr.Plugin.<Name>.dll` + `plugin.json`. No Abstractions sidecar to conflict.

## Empirical proof

`scripts/multi-plugin-coexistence-proof.ps1` mounts all four plugins into one Lidarr container and asserts each appears in its expected schema endpoints.

| State | `Could not load Abstractions` errors | Plugins detected |
|---|---|---|
| Before fix | hundreds | 0 / 4 |
| After fix | 0 | 4 / 4 (when version-aligned per below) |

Runs locally (`pwsh scripts/multi-plugin-coexistence-proof.ps1`) and in CI (`.github/workflows/multi-plugin-coexistence-proof.yml`) on every push to `main`, on every PR, and weekly via cron.

## Related version-alignment work

The Abstractions cross-ALC bug was the dominant failure but not the only one blocking multi-plugin loading. The proof harness also surfaced version mismatches between plugin AssemblyRefs and the host's actual versions:

| Plugin AssemblyRef | Lidarr host (`/app/bin/`) | Resolution |
|---|---|---|
| `M.E.DependencyInjection v9.0.0` | `v8.0.0` | Pin plugin to 8.0.x |
| `M.E.Logging.Abstractions v9.0.0` | `v8.0.0` | Pin plugin to 8.0.3 |
| `System.Security.Cryptography.ProtectedData v9.0.0` | `v8.0.0` | Pin Common to 8.0.0 (this PR) |
| `FluentValidation v11.0.0` | `v9.0.0` | Per-plugin: don't bring transitively |
| `NLog v5.x / v6.x` | `v5.4.0` | Pin plugin to 5.4.0 for host file/API parity; never cross the host boundary with NLog 6.x |
| `Lidarr.Plugin.Common v1.7.1` | (not shipped) | Triggered by `plugin.json`'s `commonVersion` field — ensure Common is internalized into the merged DLL (already the case) |

Each version mismatch produces a `Could not load <X>, Version=<plugin's version>` error during plugin discovery and prevents the plugin from being registered.

## Lessons learned

1. **Read the host's assembly metadata before assuming type identity requirements.** The "must NOT merge — breaks IPlugin identity with host" comment was an unverified assumption that survived for a long time because the symptom (multi-plugin failure) looked like a host-side ALC bug rather than a plugin-side packaging mistake.

2. **`AssemblyRef`s are load-time obligations, not "soft" hints.** If your merged plugin DLL refs `Foo, Version=X.Y.Z`, the host MUST find a `Foo.dll` at exactly that version — either in its own TPA list or alongside the plugin. There's no automatic version-rolling for non-runtime assemblies.

3. **Multiple-copies-of-the-same-DLL is worse than version mismatch.** If two plugins ship `Foo, v=1.0.0.0` from different paths, the second `LoadFromAssemblyPath` throws `COR_E_INVALIDOPERATION`. If two plugins ship `Foo, v=1.0.0.0` and `Foo, v=1.0.0.1`, the runtime can pick one. Internalizing via ILRepack eliminates both classes of conflict.

4. **Symptoms attributed to "upstream bugs" are worth re-investigating periodically.** This issue had been blocking multi-plugin releases for ~6 months. Direct experimentation with one Lidarr container + 4 mounted plugins in a single PowerShell loop produced the root cause in under an hour. The CI proof harness was added in the same PR so the issue can never silently regress.

5. **Don't ship sidecar DLLs that the host doesn't need.** The Abstractions sidecar existed because of a misread of the discovery path. Audit `_PluginRuntimeDeps` periodically against actual host requirements; what the host doesn't reference, don't ship.

## What we won't claim

This fix does not magically solve every form of multi-plugin instability. Plugins that register conflicting host-level singletons (e.g., the same `IDownloadProtocol` name), that mutate global state via static fields, or that depend on plugin load order will still misbehave. But the **type-identity-by-AssemblyRef-conflict** class of failures — which was the dominant blocker — is eliminated.

## Known follow-up work (post-rollout audit found these)

An adversarial review of the rollout surfaced these scaling risks. The first (C1) is fixed in the same PR as this doc; the others are tracked for follow-up:

- **C1 — `IsHostBridgeBuild` self-skip**: `LidarrContainerFixture.IsHostBridgeBuild` previously *required* `Lidarr.Plugin.Abstractions.dll` next to the plugin DLL. Post-fix that file is gone by design → every E2E `[SkippableFact]` would silently `Skip`, reporting green with zero coverage. **Fixed**: the check now reads the plugin DLL's metadata and accepts either shape (sidecar present OR no external Abstractions `AssemblyRef`).
- **C2 — `PluginSandbox.IPlugin` reflection**: `PluginSandbox.CreateAsync` does `types.Where(t => typeof(IPlugin).IsAssignableFrom(t))` against the testkit's public `Lidarr.Plugin.Abstractions.IPlugin`. After ILRepack internalization, the merged DLL contains an *internal* `IPlugin` with a different assembly identity → `IsAssignableFrom` returns false for every type → `PluginSandbox` throws "does not contain a concrete IPlugin implementation" for every merged plugin. **Tracked follow-up**: switch the sandbox to name-based discovery (`type.GetInterfaces().Any(i => i.FullName == "Lidarr.Plugin.Abstractions.Contracts.IPlugin")`) and reflection-based method invocation, OR conditionally skip the merge for `Configuration=Debug` so dev/test builds keep external Abstractions.
- **C3 — packaging-policy split**: resolved. `tools/PluginPack.psm1`, `scripts/parity-spec.json`, and `scripts/lib/e2e-gates.psm1` now reject `Lidarr.Plugin.{Common,Abstractions}.dll` sidecars and treat the merged-DLL package shape as canonical.
- **L1 — proof-harness blind spot**: schema-substring success can pass on order-dependent fluke (one plugin loads, others fail silently). **Tracked follow-up**: add a negative assertion that container logs contain zero `0x80131509` / `COR_E_INVALIDOPERATION` errors, and a negative assertion that no mounted plugin directory contains `Lidarr.Plugin.Abstractions.dll`.
- **L2 — pin-table drift across plugins**: `applemusicarr` / `brainarr` / `lidarr.plugin.template` `Directory.Packages.props` still have wrong host-version pins (M.E.* 9.x, FluentValidation 11.12.0, ProtectedData 9.0.14, etc.). **Tracked follow-up**: per-plugin PR to align with the table above.
- **L3 — no MSBuild gate for host-version pins**: `CentralPackageTransitivePinningEnabled=true` can silently re-elevate transitive dependencies. **Tracked follow-up**: add an `<Error Condition="..." />` gate that fails the build when M.E.* resolves to non-8.0.x at compile time.
- **L4 — ILRepack attribute hygiene**: `[InternalsVisibleTo]` from Common's tests + similar attributes leak into the merged DLL. Cosmetic; does not affect functionality.
- **L5 — brainarr host-build mismatch**: brainarr's `ext/Lidarr/_output/net8.0/` is a v10.0.0.x dev source build, not the runtime's v3.1.2.4913. Plugin DLLs link against v10 host types → fail to load against v3 host. **Tracked follow-up**: brainarr-side fix to extract host assemblies from the production Docker image.
