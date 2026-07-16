using System;
using System.IO;
using System.Reflection;
using Lidarr.Plugin.Common.TestKit.Compliance;
using Xunit;

namespace MyPlugin.Tests;

/// <summary>
/// Ecosystem parity guard - the same structural + behavior contract every RicherTunes
/// plugin repo runs (see tidalarr/qobuzarr for the adoption pattern). The inherited
/// <c>AllParityChecksPass</c> fact runs the full aggregate: Directory.Build.props /
/// Directory.Packages.props / plugin.json / global.json structure PLUS the behavior
/// contracts (no plugin-local token-store/cache/config-path forks, AddBridgeDefaults
/// registered, file-class name parity, album-completion policy, ...).
/// </summary>
/// <remarks>
/// The <see cref="PluginAssembly"/> override is load-bearing: without it 14 of the 16
/// behavior checks silently skip (they are opt-in per plugin so submodule pin bumps
/// cannot break legacy plugins). Never remove it "to fix a red build" - fix the drift
/// it found instead.
/// </remarks>
[Trait("Category", "Parity")]
public class MyPluginEcosystemParityTests : EcosystemParityTestBase
{
    /// <summary>Repo root: tests/MyPlugin.Tests/bin/{Config}/net8.0 is five levels down.</summary>
    protected override string RepoRootPath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    protected override string PluginId => "MyPlugin";

    protected override string PluginJsonRelativePath => "plugin.json";

    /// <summary>
    /// Opt-in to the behavior-contract checks (token store, response cache, bridge
    /// defaults, capability backing types, config-path roots, download-client guards).
    /// </summary>
    protected override Assembly? PluginAssembly => typeof(MyPluginPlugin).Assembly;
}
