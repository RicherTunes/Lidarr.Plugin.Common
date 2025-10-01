# Plugin Isolation

Plugins load alongside the Lidarr host but live inside their own AssemblyLoadContext (ALC). The host shares only the stable `Lidarr.Plugin.Abstractions` contract; every plugin ships its own copy of `Lidarr.Plugin.Common` and private dependencies.

```mermaid
documentation
flowchart LR
  Host[Host Process\n(Default ALC)]
  ALC1[Plugin A ALC\n(collectible)]
  ALC2[Plugin B ALC\n(collectible)]
  Host -->|Abstractions| ALC1
  Host -->|Abstractions| ALC2
  ALC1 -->|Common v1.x (private)| ALC1
  ALC2 -->|Common v1.y (private)| ALC2
```

## Minimal loader

The host uses `AssemblyDependencyResolver` to pull plugin-local assemblies while sharing the ABI assemblies with the default context.

```csharp file=../examples/IsolationHostSample/Program.cs#alc-loader
```

## Failure modes & debugging tips

- **Default ALC bleed:** Calling `Assembly.Load("Lidarr.Plugin.Common")` will load into the default context. Always resolve using `AssemblyDependencyResolver` and `LoadFromAssemblyPath`.
- **File locks on Windows:** Static references or unmanaged handles keep the ALC alive. Dispose plugin services, call `GC.Collect()`/`WaitForPendingFinalizers()` twice, and ensure no static holds host services.
- **apiVersion mismatch:** If the manifest declares an incompatible `apiVersion`, the loader must refuse to load (see [PLUGIN_MANIFEST.md](PLUGIN_MANIFEST.md)).
- **Shared singleton collisions:** Keep plugin state scoped to the plugin ALC. Avoid global singletons in Common that capture host services.

## Optional shim pattern

When the host loader cannot create custom ALCs, ship a stub plugin that immediately spins up its own ALC and forwards calls.

```csharp file=../examples/IsolationHostSample/Program.cs#shim-plugin
```

## Contract

- Host-owned assemblies (`Lidarr.Plugin.Abstractions`, logging abstractions) load in the **default ALC**.
- Every plugin loads inside a **collectible ALC** created per plugin directory.
- Only types defined in `Lidarr.Plugin.Abstractions` cross the boundary.
- `apiVersion` major mismatch **aborts** plugin load and logs a friendly error.
- Unloading a plugin must leave no file locks after two GC cycles.
