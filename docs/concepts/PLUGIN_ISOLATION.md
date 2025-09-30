# Plugin Isolation

Keep one assembly contract shared (`Lidarr.Plugin.Abstractions`) and isolate everything else per plugin. This page is the single source of truth for how the host and plugins cooperate across AssemblyLoadContexts (ALCs).

## Overview

- **Why**: .NET only loads a single assembly per simple name inside any ALC. Without isolation, the first plugin to load `Lidarr.Plugin.Common` wins and the rest crash when APIs differ.
- **How**: The host loads the ABI (`Lidarr.Plugin.Abstractions`) once in the default ALC and spawns a *collectible* ALC per plugin. Each plugin ships its own `Lidarr.Plugin.Common` and private dependencies inside its folder.
- **Result**: Plugin A can run Common 1.1.4 while Plugin B sticks to Common 1.0.9 without conflicts or binding redirects.

```mermaid

flowchart LR
  Host[Host Process\n(Default ALC)]
  ALC1[Plugin A ALC\nCommon 1.1.4]
  ALC2[Plugin B ALC\nCommon 1.0.9]

  Host -- Abstractions --> ALC1
  Host -- Abstractions --> ALC2
  ALC1 -- Plugin-private deps --> ALC1
  ALC2 -- Plugin-private deps --> ALC2

```

## Host responsibilities

1. **Share only contracts**
   - Reference `Lidarr.Plugin.Abstractions`.
   - Treat it as an ABI: keep it loaded in the default ALC.
2. **Create a collectible ALC per plugin**
   - Use `AssemblyDependencyResolver` so dependency probing stays inside the plugin folder.
   - Share `Microsoft.Extensions.Logging.Abstractions` (and other narrow abstractions) when cross-ALC casting is required.
3. **Validate compatibility before load**
   - Read `plugin.json` with `PluginManifest.Load`.
   - Reject on `apiVersion` mismatch or if `HostVersion` < `minHostVersion`.
4. **Unload cleanly**
   - Call `PluginHandle.DisposeAsync()` and then `GC.Collect` → `WaitForPendingFinalizers` twice to release file locks on Windows.

```csharp file=../../examples/IsolationHostSample/Program.cs#alc-loader

```

> The full loader sample lives in [`examples/IsolationHostSample`](../../examples/IsolationHostSample/).

## Plugin packaging checklist

| Step | Why | Implementation |
|------|-----|----------------|
| Reference Abstractions as compile-time only | Host owns the runtime copy | `<PackageReference Include="Lidarr.Plugin.Abstractions" PrivateAssets="all" ExcludeAssets="runtime;native;contentfiles" />` |
| Ship Common + private deps in plugin folder | Keeps each plugin isolated | `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` or `dotnet publish` |
| Author `plugin.json` | Host performs ABI/host checks | See [Manifest reference](../reference/MANIFEST.md) |
| Avoid leaking Common types | Cross-ALC casts fail otherwise | Expose only Abstractions DTOs across boundaries |

```text
/Plugins/MyPlugin/
  MyPlugin.dll
  Lidarr.Plugin.Common.dll
  ThirdPartyA.dll
  plugin.json
```

## Failure modes

| Symptom | Root cause | Fix |
|---------|------------|-----|
| `MissingMethodException` during startup | Host loaded wrong Common version in default ALC | Ensure plugins never call `Assembly.Load("Lidarr.Plugin.Common")`; rely on `PluginLoadContext` with `AssemblyDependencyResolver` |
| Host refuses plugin with "abstractions major" message | `apiVersion` in `plugin.json` does not match host Abstractions major | Rebuild plugin against the current Abstractions package and update manifest |
| Plugin cannot unload on Windows (file in use) | Strong references to plugin types remain in host static state | Dispose plugin, null out references, then run GC collect/finalize cycle |
| `FileNotFoundException` for dependency | Plugin folder missing dependency or CopyLocal disabled | Enable `CopyLocalLockFileAssemblies` or include dependency in payload subfolder |

## Alternative: shim/proxy plugin
If the host loader cannot change, ship a stub that runs inside the hosts default ALC, spins up a private ALC, and forwards every interface call to the payload living in a subfolder.

```csharp file=../../examples/IsolationHostSample/Program.cs#shim-plugin

```

Payload layout:

```text
/Plugins/MyShim/
  ShimPlugin.dll            # host loads this (no Common reference)
  plugin.json
  /payload/
    RealPlugin.dll          # references Common
    Lidarr.Plugin.Common.dll
    third-party.dll
```

## Contract

- The host loads **exactly one** copy of `Lidarr.Plugin.Abstractions` in the default ALC.
- Each plugin is loaded in its **own** collectible ALC; no Common assemblies enter the default ALC.
- Compatibility gates: `apiVersion` major must match, `HostVersion` must satisfy `minHostVersion`.
- Plugins may assume the host shares `ILoggerFactory` via Abstractions, but no other host services cross the boundary unless declared in Abstractions.
- Unloading disposes the plugin, unloads the ALC, and releases file locks after two GC cycles.

## Further reading

- [Architecture](ARCHITECTURE.md)
- [Manifest reference](../reference/MANIFEST.md)
- [Host loader sample](../../examples/IsolationHostSample/)
- [Isolation tests](../../tests/Isolation/)



