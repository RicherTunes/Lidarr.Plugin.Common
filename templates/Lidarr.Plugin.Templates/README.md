# Lidarr Plugin Templates

This package provides a `dotnet new` template to bootstrap a Lidarr streaming plugin.

Usage (once published):

  dotnet new install RicherTunes.Lidarr.Plugin.Templates
  dotnet new lidarr-plugin -n MyStreamingServiceArr

Generated projects restore `Lidarr.Plugin.Common`, `Lidarr.Plugin.Abstractions`,
and `Lidarr.Plugin.Common.TestKit` from configured NuGet sources. Publish those
packages to the same feed before publishing the template package, or build the
generated project with `-p:LidarrPluginCommonRepoRoot=/path/to/Lidarr.Plugin.Common`.
The generated smoke loads the scaffold with `PluginSandbox`; it does not prove that
the author has implemented real Lidarr indexer/download-client adapters.

Local install (from repo):

  dotnet new install templates/lidarr-plugin
  dotnet new lidarr-plugin -n LocalServiceArr

Local source validation:

  pwsh scripts/ci/smoke-lidarr-plugin-template.ps1

The validation script packs Common, Abstractions, TestKit, and the template into a
temporary local NuGet source, materializes a fresh plugin, builds it, and runs the
generated tests. Use that path when changing the scaffold; a direct in-repo project
build does not exercise the package that `dotnet new` ships.
