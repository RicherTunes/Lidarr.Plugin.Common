# Isolation Host Sample

`examples/IsolationHostSample` is a runnable console app that loads plugins into separate AssemblyLoadContexts. Use it to smoke test new plugins or validate host changes.

## Run it
```bash
dotnet run --project examples/IsolationHostSample -- "path/to/plugins"
```
If no path is provided it looks for a `plugins` folder next to the executable.

## What it demonstrates
- Reading `plugin.json` and enforcing compatibility gates.
- Sharing only `Lidarr.Plugin.Abstractions` and logging abstractions with the host.
- Creating a `PluginLoadContext` per plugin and disposing it cleanly.
- Printing plugin manifest information (id, version, Common version).

## Snippets
- [ALC loader](../concepts/PLUGIN_ISOLATION.md#host-responsibilities)
- [Shim example](../concepts/PLUGIN_ISOLATION.md#alternative-shimproxy-plugin)

## Extend it
- Add command-line options to reload on file changes.
- Integrate with the test builder (`tests/Isolation/TestPluginBuilder.cs`) to generate temporary plugins during CI.
- Capture load/unload metrics for observability experiments.
