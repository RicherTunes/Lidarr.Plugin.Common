# How-to: Create a Plugin Project

Follow these steps every time you start a new plugin. This guide keeps the project structure aligned with the isolation architecture.

## 1. Start from the template

```bash

mkdir MyPlugin && cd MyPlugin
dotnet new classlib -n MyPlugin -f net8.0

```

Optional: add a solution file so you can include tests.

## 2. Configure the project file

```xml

<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Lidarr.Plugin.Abstractions" Version="1.2.2" PrivateAssets="all" ExcludeAssets="runtime;native;contentfiles" />
    <PackageReference Include="Lidarr.Plugin.Common" Version="1.2.2" />
  </ItemGroup>
</Project>

```

**Why these settings?**

- `CopyLocalLockFileAssemblies` ensures every dependency (including Common) is copied next to the plugin DLL.
- `PrivateAssets="all"` prevents the plugin from accidentally shipping another copy of the Abstractions assembly.

## 3. Add a manifest file
Create `plugin.json` in the project root (mark as `Copy to Output Directory = Always`):

```json

{
  "id": "myplugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "apiVersion": "1.x",
  "commonVersion": "1.2.2",
  "minHostVersion": "2.12.0",
  "entryAssembly": "MyPlugin.dll"
}

```

See the [manifest reference](../reference/MANIFEST.md) for every field.

## 4. Implement the plugin entry point

- Derive from `StreamingPlugin<TModule, TSettings>` to get a ready-made `IPlugin` bridge.
- Return indexer/download adapters from `CreateIndexerAsync` and `CreateDownloadClientAsync`.
- Document settings via `DescribeSettings()` and validate them in `ValidateSettings()`.
- Keep Common-dependent code inside the plugin AssemblyLoadContext.
- For a full example see [Use the streaming plugin bridge](USE_STREAMING_PLUGIN.md).

## 5. Add tests early

- Reference the shared tests project or create your own `MyPlugin.Tests` project.
- Use `dotnet test` to validate business logic.
- Use the Roslyn builder (`tests/Isolation/TestPluginBuilder.cs`) for integration tests if your plugin ships internal assemblies.

## 6. Publish

```bash

dotnet publish -c Release -o out

```

Copy the output to the host’s plugin directory or package it per distribution rules.

## Checklists

- [ ] Project references Abstractions (compile time) and Common (runtime).
- [ ] Manifest copied to output.
- [ ] Common + third-party dependencies present in build output.
- [ ] Tests run cleanly.
- [ ] README/notes updated with any settings or prerequisites.

Next steps: implement features via the other how-to guides (indexer, download client, OAuth, logging).


