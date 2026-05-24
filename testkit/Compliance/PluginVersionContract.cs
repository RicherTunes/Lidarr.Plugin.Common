using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Static assertions catching version drift between sources of truth that exist
/// in every Lidarr plugin in the RicherTunes family.
///
/// <para>Background:</para> sibling plugins have hit silent version drift in
/// multiple ways:
/// <list type="bullet">
///   <item>brainarr's <c>AssemblyInfo.cs</c> hardcoded <c>[assembly: AssemblyVersion("1.3.2.0")]</c>
///         literal stayed through 1.4.0 and 1.4.1 releases (because
///         <c>&lt;GenerateAssemblyInfo&gt;false&lt;/GenerateAssemblyInfo&gt;</c> made
///         Directory.Build.props' VERSION-file-driven version inert). Result:
///         <c>/api/v1/system/plugins</c> reported installedVersion=1.3.2 while the
///         actual release was 1.4.1.</item>
///   <item>tidalarr's <c>TidalModule.Version = "1.1.0"</c> const literal stayed through
///         1.1.1 (caught by VersionContract).</item>
///   <item>applemusicarr's <c>manifest.json</c> "0.3.0-beta.2" stayed through 0.4.0
///         (also caught by VersionContract).</item>
/// </list>
///
/// <para>Usage:</para> per-plugin test class becomes ~5 lines per assertion:
/// <code>
/// public class BrainarrVersionContractTests
/// {
///     [Fact] public void AssemblyMatchesPluginJson() =>
///         PluginVersionContract.AssertAssemblyVersionMatchesPluginJson(typeof(BrainarrInstalledPlugin));
///
///     [Fact] public void VersionFileMatchesPluginJson() =>
///         PluginVersionContract.AssertVersionFileMatchesPluginJson(typeof(BrainarrInstalledPlugin));
///
///     [Fact] public void ManifestMatchesPluginJson() =>
///         PluginVersionContract.AssertManifestMatchesPluginJson(typeof(BrainarrInstalledPlugin));
/// }
/// </code>
///
/// Plugins that don't ship a <c>manifest.json</c> (e.g. qobuzarr) simply omit the
/// <c>AssertManifestMatchesPluginJson</c> call.
/// </summary>
public static class PluginVersionContract
{
    /// <summary>
    /// Assert the plugin assembly's <see cref="AssemblyName.Version"/> (3-part) equals
    /// <c>plugin.json</c>'s top-level <c>"version"</c> field.
    /// </summary>
    /// <param name="pluginTypeAnchor">Any type from the plugin assembly. Used to locate
    /// <c>plugin.json</c> via <see cref="AppContext.BaseDirectory"/> or via repo walk
    /// from the assembly's location.</param>
    /// <param name="pluginJsonPath">Optional explicit path to <c>plugin.json</c>.</param>
    public static void AssertAssemblyVersionMatchesPluginJson(Type pluginTypeAnchor, string? pluginJsonPath = null)
    {
        var asm = pluginTypeAnchor.Assembly;
        var asmVersion = asm.GetName().Version?.ToString(3);
        Assert.False(string.IsNullOrWhiteSpace(asmVersion),
            $"Assembly {asm.GetName().Name} must declare a non-empty Version.");

        pluginJsonPath ??= LocatePluginJson(asm);
        Skip.If(pluginJsonPath is null, "plugin.json not found in AppContext.BaseDirectory or any parent directory.");

        var pluginJsonVersion = ReadJsonVersion(pluginJsonPath!);
        Assert.False(string.IsNullOrWhiteSpace(pluginJsonVersion),
            $"plugin.json at {pluginJsonPath} must declare a top-level \"version\" field.");

        Assert.Equal(pluginJsonVersion, asmVersion);
    }

    /// <summary>
    /// Assert the top-level <c>VERSION</c> file's contents equal <c>plugin.json</c>'s
    /// top-level <c>"version"</c> field.
    /// </summary>
    public static void AssertVersionFileMatchesPluginJson(Type pluginTypeAnchor)
    {
        var asm = pluginTypeAnchor.Assembly;
        var versionFilePath = LocateRepoFile(asm, "VERSION");
        var pluginJsonPath = LocatePluginJson(asm);
        Skip.If(versionFilePath is null || pluginJsonPath is null,
            "VERSION or plugin.json not found — only enforced for repo-rooted runs.");

        var versionFileContent = File.ReadAllText(versionFilePath!).Trim();
        var pluginJsonVersion = ReadJsonVersion(pluginJsonPath!);

        Assert.Equal(versionFileContent, pluginJsonVersion);
    }

    /// <summary>
    /// Assert <c>manifest.json</c>'s top-level <c>"version"</c> field equals
    /// <c>plugin.json</c>'s top-level <c>"version"</c> field.
    /// </summary>
    /// <param name="manifestRelativePath">
    /// Path to manifest.json relative to the repo root. Defaults to <c>"manifest.json"</c>;
    /// pass e.g. <c>"src/AppleMusicarr.Plugin/manifest.json"</c> when the manifest lives in
    /// a project subdirectory rather than the repo root.
    /// </param>
    /// <param name="pluginJsonRelativePath">Same convention; defaults to <c>"plugin.json"</c>.</param>
    public static void AssertManifestMatchesPluginJson(
        Type pluginTypeAnchor,
        string manifestRelativePath = "manifest.json",
        string pluginJsonRelativePath = "plugin.json")
    {
        var asm = pluginTypeAnchor.Assembly;
        var manifestPath = LocateRepoFile(asm, manifestRelativePath);
        var pluginJsonPath = LocateRepoFile(asm, pluginJsonRelativePath);
        Skip.If(manifestPath is null || pluginJsonPath is null,
            $"{manifestRelativePath} or {pluginJsonRelativePath} not found — only enforced for repo-rooted runs.");

        var manifestVersion = ReadJsonVersion(manifestPath!);
        var pluginJsonVersion = ReadJsonVersion(pluginJsonPath!);

        Assert.Equal(pluginJsonVersion, manifestVersion);
    }

    // -- helpers -------------------------------------------------------------

    /// <summary>
    /// Locate <c>plugin.json</c> by walking up from the SDK's default output dir
    /// (the test's <see cref="AppContext.BaseDirectory"/>) — production builds copy
    /// plugin.json next to the test DLL, and CI runs from a repo root.
    /// </summary>
    private static string? LocatePluginJson(Assembly _) =>
        Path.Combine(AppContext.BaseDirectory, "plugin.json") switch
        {
            var p when File.Exists(p) => p,
            _ => LocateRepoFile(_, "plugin.json"),
        };

    /// <summary>Walks parents from <see cref="AppContext.BaseDirectory"/> looking for <paramref name="relativePath"/>.</summary>
    private static string? LocateRepoFile(Assembly _, string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string ReadJsonVersion(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("version").GetString() ?? string.Empty;
    }
}
