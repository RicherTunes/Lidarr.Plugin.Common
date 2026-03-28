# Host Version Upgrade Playbook

## When to use
When upgrading the Lidarr host target (e.g., pr-plugins-3.1.2.4913 → next version).

## Steps

1. **Update Common**
   - Change DefaultHostVersion in PluginSandbox.cs
   - Update ARCHITECTURE_STATUS.md baseline
   - Update ECOSYSTEM_PROMOTION_CHECKLIST.md
   - Tag patch release if needed

2. **Update each plugin**
   - CLAUDE.md Docker image references
   - CI workflow Docker image tags
   - verify-local.ps1 / build scripts
   - DockerSmokeTests.cs image constant

3. **Extract new host assemblies**
   - `crane export ghcr.io/hotio/lidarr:<new-tag> /tmp/lidarr-image.tar`
   - Extract DLLs, verify FV version, verify net8.0 target

4. **Run promotion matrix**
   - All 107 tests must pass against new host
   - Docker smoke must pass (if host-bridge build available)

5. **Freeze**
   - Update ARCHITECTURE_STATUS.md with new baseline
   - Bump all plugins to same Common tag
