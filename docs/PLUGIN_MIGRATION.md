# Plugin Migration Checklist (Isolation-Ready)

Use this guide to move an existing Lidarr streaming plugin onto the new Abstractions/Common split with per-plugin AssemblyLoadContexts.

## 1. Update package references
- Add the Abstractions contract with compile-time only metadata:
  ```xml
  <PackageReference Include="Lidarr.Plugin.Abstractions" Version="1.0.0" PrivateAssets="all" ExcludeAssets="runtime;native;contentfiles" />
  ```
- Reference `Lidarr.Plugin.Common` normally (each plugin ships its own version).
- Enable `CopyLocalLockFileAssemblies` or use `dotnet publish` so **all** runtime dependencies sit beside the plugin DLL.

## 2. Ship a manifest (`plugin.json`)
Example:
```json
{
  "id": "myplugin",
  "name": "My Plugin",
  "version": "2.3.0",
  "apiVersion": "1.x",
  "commonVersion": "1.1.4",
  "minHostVersion": "2.12.0",
  "entryAssembly": "MyPlugin.dll"
}
```
- `apiVersion` must match the Abstractions major that the plugin was built against.
- `minHostVersion` is the minimum Lidarr host version you support.
- `commonVersion` is informational; it helps diagnose mixed dependency sets.

See `docs/PLUGIN_MANIFEST.md` for the full schema and validation rules.

## 3. Make sure only Abstractions types cross the boundary
- Public plugin entry points (`IPlugin`, `IIndexer`, `IDownloadClient`, DTOs, settings providers) should use Abstractions types only.
- Do **not** return or accept `Lidarr.Plugin.Common` types from these interfaces; keep them internal to your plugin AssemblyLoadContext.

## 4. Validate with the isolation tests
- Run the shared test suite in this repository (`dotnet test`).
- Optionally use `tests/Isolation/TestPluginBuilder` as a template to confirm your plugin loads side-by-side with other Common versions.
- Use the sample loader in `examples/IsolationHostSample/` to do a smoke test against a real directory layout.

## 5. Deployment layout
```
/Plugins/MyPlugin/
  MyPlugin.dll
  Lidarr.Plugin.Common.dll
  ThirdPartyA.dll
  plugin.json
```
Each plugin lives in its own folder with its private dependencies. The host shares only `Lidarr.Plugin.Abstractions` and optional logging abstractions.

## 6. Optional: Shim if the host cannot change
When the host loader is fixed, ship a stub that spins up a private AssemblyLoadContext. See `docs/PLUGIN_ISOLATION.md` for the full shim example.

Following these steps ensures your plugin survives side-by-side loading, hot reloads, and future host upgrades without hard process restarts.
