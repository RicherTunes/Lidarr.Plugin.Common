# Upgrading Lidarr.Plugin.Common

Every tagged release ships with this checklist so plugin authors (and downstream repos like Brainarr) can bump confidently.

## Quick summary
- Read the release note or the top entry in [CHANGELOG.md](../CHANGELOG.md) for a one-paragraph overview of breaking or noteworthy changes.
- Confirm the tag follows the semantic `vMAJOR.MINOR.PATCH` pattern. Release automation only publishes when the tag is present and CI is green.

## Before you upgrade
1. Scan the **API changes** section in the release note. When we add or remove public APIs, the PublicAPI baselines are updated so `dotnet format` and `apicompat` stay quiet.
2. Check the **Dependencies** section for new package requirements or minimum host versions.
3. Review the **Migration** bullets for any configuration or behaviour adjustments.

## After you upgrade
1. Run `dotnet restore` in your plugin repository.
2. Execute your plugin test suite (start with the streaming harness from [docs/how-to/USE_STREAMING_PLUGIN.md](how-to/USE_STREAMING_PLUGIN.md)).
3. Verify packaging remains quiet by calling `New-PluginPackage` (default folder packaging) or `-MergeAssemblies` if you ship a single DLL.

## Release note template (maintainers)
When cutting a release, append the following to the changelog and include it in the GitHub release description:

```
### Summary
- Highlight the headline change in one sentence.

### API changes
- List added/removed/renamed types or methods (link to PublicAPI diff when possible).

### Dependencies
- Note new package references, minimum Lidarr host version, or .NET TFMs.

### Migration
- Steps plugin authors must take (config keys, manifest fields, etc.).
```

Keeping this file up to date ensures every consumer sees the same guidance without digging through commit history.
