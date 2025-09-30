# Quickstart: Load Plugins Safely

This guide is for host maintainers who need to load third-party plugins without version conflicts.

## Prerequisites
- .NET 8 host application.
- `Lidarr.Plugin.Abstractions` package bundled with the host.
- Plugin directories containing plugin DLLs, private dependencies, and `plugin.json` files.

## 1. Reference the ABI
Add a project reference (if building from source) or bundle the package with your host. Abstractions must reside in the default AssemblyLoadContext.

## 2. Discover plugins
Scan a root directory (`/Plugins/*`) for manifest files. Maintain metadata (id, path, last modified) so you can reload when files change.

## 3. Validate compatibility
Use `PluginManifest.Load(path)`:
- Reject when `apiVersion` major does not match your Abstractions major.
- Reject when `HostVersion` is below `minHostVersion`.
- Surface friendly, actionable errors to the user.

## 4. Spin up a collectible ALC
```csharp file=../../examples/IsolationHostSample/Program.cs#alc-loader
```
Key points:
- Share only `Lidarr.Plugin.Abstractions` (and optional logging abstractions).
- Use `AssemblyDependencyResolver` so dependencies load from the plugin folder.
- Wrap the plugin and ALC inside `PluginHandle` for deterministic cleanup.

## 5. Unload and reload
When a plugin updates:
1. Dispose the handle (`await handle.DisposeAsync()`).
2. Remove references to plugin types.
3. Trigger `GC.Collect()`, `GC.WaitForPendingFinalizers()`, `GC.Collect()`.
4. Re-run discovery + validation.

## 6. Instrument and log
- Tag logs with plugin id/version so support can diagnose issues.
- Measure load time and exceptions.
- Track last successful unload to detect slow/shim plugins.

## 7. Automate testing
- Run [`tests/PluginIsolationTests`](../../tests/PluginIsolationTests.cs) as part of CI to validate isolation.
- Add integration tests that load multiple plugins with different Common versions using the Roslyn builder (`tests/Isolation/TestPluginBuilder.cs`).

## Troubleshooting
See the [failure modes table](../concepts/PLUGIN_ISOLATION.md#failure-modes) for common issues like `MissingMethodException` or file locks.

You now have an isolation-friendly host loader that keeps plugin ecosystems healthy.




