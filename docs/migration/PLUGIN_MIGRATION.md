# Migration: Legacy Plugin → Streaming Bridge

Follow this checklist when migrating an existing plugin that previously referenced `Lidarr.Plugin.Common` directly in the default AssemblyLoadContext.

1. **Split contracts**
   - Reference `Lidarr.Plugin.Abstractions` for interfaces/DTOs only (compile-time dependency, `PrivateAssets="all"`).
   - Move everything else to the plugin project and ship it privately.
   - Map your manifest to the [schema](../PLUGIN_MANIFEST.md) and set the correct `apiVersion`.

2. **Adopt the bridge**
   - Derive your entry point from `StreamingPlugin<TModule,TSettings>` (see [PLUGIN_BRIDGE.md](../PLUGIN_BRIDGE.md)).
   - Register services inside a module; use constructor injection instead of service locators.
   - Replace static singletons with scoped services inside the plugin ALC.

3. **Type-safe settings**
   - Create a POCO settings class with sane defaults.
   - Use the dot-notation mapping rules from [SETTINGS_PROVIDER.md](../SETTINGS_PROVIDER.md).
   - Validate settings in `ValidateSettings` and return actionable messages.

4. **Ensure ALC isolation**
   - Verify the manifest lives alongside the plugin.
   - Use the loader pattern from [PLUGIN_ISOLATION.md](../PLUGIN_ISOLATION.md) in host tests.
   - Confirm two plugin versions can load simultaneously without type conflicts.

5. **Update packaging**
   - Adopt `PluginPack.psm1` or the shared GitHub Action to publish artifacts (see [PACKAGING.md](../PACKAGING.md)).
   - Exclude host-owned assemblies from the plugin folder.

6. **Rebuild tests**
   - Add coverage for gzip sniffing, retry policies, fallback, and unload semantics (see [TESTING_WITH_TESTKIT.md](../TESTING_WITH_TESTKIT.md)).
   - Include manifest validation in CI.

7. **Finalize**
   - Update documentation in your plugin repository to reflect the new manifest + bridge requirements.
   - Communicate any breaking changes to users (new settings, host requirements, etc.).

## Contract

- Every migrated plugin must supply a valid `plugin.json` and respect the Abstractions major version.
- Plugins must no longer load `Lidarr.Plugin.Common` into the default ALC.
- Packaging must run the shared manifest validation step in CI before publishing.
- Tests must cover at least isolation, gzip sniffing, and request timeout handling.
