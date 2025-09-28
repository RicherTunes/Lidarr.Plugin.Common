# Unified Plugin Pipeline

This document captures the governance model for versioning plugins that consume `Lidarr.Plugin.Common` alongside official Lidarr host binaries. The intent is to keep every plugin aligned to the same host assemblies, the same shared library commit, and the same packaging rules.

## 1. Single Source of Truth Repository
- Stand up a `plugins-platform` repository.
- Pin the blessed commit of `Lidarr.Plugin.Common` (currently `52ffc434db552aac24c21f72d95c197773523beb`).
- Store the shared `Directory.Build.props` that freezes:
  - `<AssemblyVersion>10.0.0.35686</AssemblyVersion>`
  - `<FileVersion>10.0.0.35686</FileVersion>`
  - Comment: `// Matches Lidarr 2.14.2.4786 host binaries`
- Ship `scripts/sync-host-assemblies.ps1` that copies host binaries from your canonical Lidarr build drop into `artifacts/host-assemblies/` and validates `AssemblyVersion` ⇔ `FileVersion`.
- Every plugin consumes this repo as a submodule to inherit build props, scripts, and assemblies.

```
git submodule add https://github.com/your-org/plugins-platform.git extern/plugins-platform
```

## 2. Version-Gated CI Workflow
Each plugin repository must:
1. Update/init the `plugins-platform` submodule.
2. Execute `scripts/sync-host-assemblies.ps1 -Target ../Lidarr/_output/net6.0` (or equivalent path).
3. Run `dotnet build -c Release`.
4. Run `dotnet test -c Release --no-build`.
5. Fail fast if the resulting `Lidarr.Plugin.Common.dll` or host assemblies report anything other than `10.0.0.35686`.

Use GitHub Actions, Azure DevOps, or similar. Example GitHub Actions steps:

```
- uses: actions/checkout@v4
  with:
    submodules: recursive

- name: Sync host assemblies
  shell: pwsh
  run: ./extern/plugins-platform/scripts/sync-host-assemblies.ps1 -Target ../Lidarr/_output/net6.0

- name: Build
  run: dotnet build -c Release -warnaserror:NU1903

- name: Test
  run: dotnet test -c Release --no-build
```

## 3. Automated ILRepack Enforcement
- Under `plugins-platform/build/`, define a reusable MSBuild target (for example `EnsureMergedDependencies.targets`).
- Target inspects `@(ResolvedFileToPublish)` after build.
- If any `Lidarr.*` or `Lidarr.Plugin.Common.dll` is present, fail the build with a descriptive message.
- Provide shared props to wire the target into every plugin (`<Import Project="$(PluginsPlatformRoot)/build/EnsureMergedDependencies.targets" />`).

## 4. Coordinated Release Job
- Create a meta-pipeline that triggers when:
  - `Lidarr.Plugin.Common` version changes, or
  - The target Lidarr host version changes.
- Steps:
  1. Update `plugins-platform` `Directory.Build.props` + host assemblies.
  2. Sequentially trigger each plugin CI (GitHub workflow dispatch, ADO pipeline, etc.).
  3. Collect artifacts.
  4. Run an end-to-end smoke test (e.g., Docker Compose with Lidarr 2.14.2.4786 + all plugins).
  5. Publish a consolidated release bundle and changelog.
- No plugin release is approved until the orchestrated run succeeds.

## 5. Artifact & Manifest Consistency
- Share `scripts/package-plugin.ps1` that:
  - Regenerates `plugin.json`.
  - Injects dependency metadata (min Lidarr version, `Lidarr.Plugin.Common` commit).
  - Produces hashes.
  - Uploads ZIP + manifest to your internal registry (S3, Azure Storage, etc.).
- CI should call this after tests and fail if metadata is missing or stale.

## 6. Monitoring & Alerting
- Schedule a recurring pipeline (daily/weekly) that:
  - Spins up Lidarr 2.14.2.4786 with the most recent plugins.
  - Scrapes logs for assembly load errors ("Could not load...").
  - Sends alerts (Slack/Teams/email) on failure.

## 7. Plugin Version Bump Playbook
1. Update `plugins-platform`:
   - Refresh host assemblies to the new Lidarr version.
   - Update `Directory.Build.props` with the new `AssemblyVersion`/`FileVersion`.
   - Tag the commit.
2. Create pull requests in every plugin repo to bump the submodule reference.
3. Ensure CI passes (sync script + ILRepack + build/test).
4. Run the coordinated release job and publish artifacts.
5. Update consumer-facing changelogs or release notes to signal the new host requirement.

Keeping this playbook centralized prevents drift, keeps plugins consistent, and eliminates crash loops caused by mismatched `Lidarr.Plugin.Common` binaries.
