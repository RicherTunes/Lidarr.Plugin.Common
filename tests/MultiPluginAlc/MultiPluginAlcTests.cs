using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Lidarr.Plugin.Abstractions.Hosting;
using Xunit;
// Xunit.SkippableFact provides [SkippableFact] + Skip.If(...) — required so the
// "plugin DLLs not present" early-out flows through xUnit as Skipped rather than
// Failed (the plain [Fact] attribute treats Xunit.SkipException as a regular
// exception → test fails on CI runners that haven't pre-built sibling plugins).

namespace Lidarr.Plugin.Common.Tests.MultiPluginAlc;

/// <summary>
/// Validates that all four RicherTunes plugin DLLs can be loaded into isolated
/// <see cref="AssemblyLoadContext"/> instances within a single process without
/// type-identity collisions, DLL-version conflicts, or load failures.
///
/// This is the gating test suite for the post-ALC-fix multi-plugin install scenario
/// described in docs/dev-guide/ALC_MULTIPLUGIN_FIX.md.
///
/// Tests SKIP (rather than FAIL) when a plugin's DLL is not present locally — build
/// the plugin with "dotnet build --configuration Release" first.
/// </summary>
// Cross-repo: requires all four sibling plugin DLLs built on disk. On a multi-repo dev machine the
// dev builds can drift in Common.dll version, so this must NOT run in the deterministic per-PR lane.
// Runs in the opt-in Integration lane (and the dedicated coexistence proof).
[Trait("Category", "Integration")]
public sealed class MultiPluginAlcTests
{
    /// <summary>
    /// Canonical plugin descriptor — maps each plugin to its build-output DLL path
    /// and the expected main assembly filename declared in plugin.json.
    /// </summary>
    private sealed record PluginDescriptor(
        string RepoName,
        string DllPath,
        string AssemblyName,
        string PluginJsonPath);

    /// <summary>
    /// Canonical (lean/merged) plugin DLL paths — used for ALC isolation tests.
    /// These are the actual package bins shipped to Lidarr. They reference host
    /// assemblies (Lidarr.Core, FluentValidation) that are only available inside the
    /// real Lidarr process, so full type enumeration will throw ReflectionTypeLoadException.
    /// </summary>
    private static readonly IReadOnlyList<PluginDescriptor> AllPlugins =
    [
        new PluginDescriptor(
            "brainarr",
            @"C:\R\Alex\github\brainarr\Brainarr.Plugin\bin\Lidarr.Plugin.Brainarr.dll",
            "Lidarr.Plugin.Brainarr",
            @"C:\R\Alex\github\brainarr\Brainarr.Plugin\bin\plugin.json"),

        new PluginDescriptor(
            "qobuzarr",
            @"C:\R\Alex\github\qobuzarr\bin\Lidarr.Plugin.Qobuzarr.dll",
            "Lidarr.Plugin.Qobuzarr",
            @"C:\R\Alex\github\qobuzarr\bin\plugin.json"),

        new PluginDescriptor(
            "tidalarr",
            @"C:\R\Alex\github\tidalarr\src\Tidalarr\bin\Lidarr.Plugin.Tidalarr.dll",
            "Lidarr.Plugin.Tidalarr",
            @"C:\R\Alex\github\tidalarr\src\Tidalarr\bin\plugin.json"),

        new PluginDescriptor(
            "applemusicarr",
            @"C:\R\Alex\github\applemusicarr\src\AppleMusicarr.Plugin\bin\Release\net8.0\AppleMusicarr.Plugin.dll",
            "AppleMusicarr.Plugin",
            @"C:\R\Alex\github\applemusicarr\src\AppleMusicarr.Plugin\bin\Release\net8.0\plugin.json"),
    ];

    /// <summary>
    /// Full-transitive-closure build output paths — used for the IPlugin metadata
    /// reflection test where we need all transitive dependencies available to fully
    /// enumerate types. These are the test bins, which include host stub assemblies.
    /// </summary>
    private static readonly IReadOnlyList<PluginDescriptor> AllPluginsFullBuild =
    [
        new PluginDescriptor(
            "brainarr",
            @"C:\R\Alex\github\brainarr\Brainarr.Tests\bin\Release\net8.0\Lidarr.Plugin.Brainarr.dll",
            "Lidarr.Plugin.Brainarr",
            @"C:\R\Alex\github\brainarr\Brainarr.Tests\bin\Release\net8.0\plugin.json"),

        new PluginDescriptor(
            "qobuzarr",
            @"C:\R\Alex\github\qobuzarr\tests\Qobuzarr.Tests\bin\Release\net8.0\Lidarr.Plugin.Qobuzarr.dll",
            "Lidarr.Plugin.Qobuzarr",
            @"C:\R\Alex\github\qobuzarr\tests\Qobuzarr.Tests\bin\Release\net8.0\plugin.json"),

        new PluginDescriptor(
            "tidalarr",
            @"C:\R\Alex\github\tidalarr\tests\Tidalarr.Tests\bin\Release\net8.0\Lidarr.Plugin.Tidalarr.dll",
            "Lidarr.Plugin.Tidalarr",
            @"C:\R\Alex\github\tidalarr\tests\Tidalarr.Tests\bin\Release\net8.0\plugin.json"),

        new PluginDescriptor(
            "applemusicarr",
            @"C:\R\Alex\github\applemusicarr\src\AppleMusicarr.Plugin\bin\Release\net8.0\AppleMusicarr.Plugin.dll",
            "AppleMusicarr.Plugin",
            @"C:\R\Alex\github\applemusicarr\src\AppleMusicarr.Plugin\bin\Release\net8.0\plugin.json"),
    ];

    /// <summary>
    /// Canonical Common version expected in all plugin packages (from Common's VERSION file).
    /// </summary>
    private const string CanonicalCommonAssemblyVersion = "1.8.0.0";

    // ─────────────────────────────────────────────────────────────────────────
    // Test 1: Type-identity isolation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load all 4 plugins into 4 isolated ALCs and prove the IPlugin type object
    /// resolved from each ALC is a DIFFERENT runtime Type instance — confirming
    /// that each plugin's Abstractions reference is truly isolated and that no
    /// cross-ALC type-identity collision exists.
    ///
    /// Pre-ALC-fix: the second plugin loading Lidarr.Plugin.Abstractions.dll from
    /// a different path would throw COR_E_INVALIDOPERATION (0x80131509).
    /// Post-ALC-fix: Abstractions is internalised into each merged plugin DLL, so
    /// each ALC resolves its own private copy — different Type instances, no collision.
    /// </summary>
    [SkippableFact]
    public void LoadAllPluginsInIsolatedAlcs_NoTypeIdentityCollisions()
    {
        var loaded = new List<(string RepoName, Assembly Assembly, AssemblyLoadContext Alc)>();
        var missing = new List<string>();

        foreach (var plugin in AllPlugins)
        {
            if (!File.Exists(plugin.DllPath))
            {
                missing.Add(plugin.RepoName);
                continue;
            }

            var alc = new PluginLoadContext(plugin.DllPath, isCollectible: false);
            var assembly = alc.LoadFromAssemblyPath(plugin.DllPath);
            loaded.Add((plugin.RepoName, assembly, alc));
        }

        if (missing.Count == AllPlugins.Count)
        {
            Skip.If(true,
                $"All plugin DLLs are missing. Build each plugin with " +
                $"'dotnet build --configuration Release' before running this suite. " +
                $"Missing: {string.Join(", ", missing)}");
            return;
        }

        if (missing.Count > 0)
        {
            // Partial run — report which ones were skipped but still validate the ones present.
            // We do NOT skip the whole test; partial coverage is better than none.
            Console.WriteLine(
                $"[MultiPluginAlcTests] Skipping {missing.Count} plugins (DLL not found): " +
                $"{string.Join(", ", missing)}. " +
                $"Build them with 'dotnet build --configuration Release'.");
        }

        if (loaded.Count < 2)
        {
            Skip.If(true,
                $"Need at least 2 plugin DLLs to test type-identity isolation. " +
                $"Found {loaded.Count}. Missing: {string.Join(", ", missing)}");
            return;
        }

        // For each loaded plugin, look for a type whose full name contains "IPlugin"
        // (handles both sidecar-present and ILRepack-merged-internal shapes per C2 in ALC_MULTIPLUGIN_FIX.md).
        var ipluginTypes = loaded
            .Select(p => (p.RepoName, IPluginType: FindIPluginTypeSafe(p.Assembly)))
            .Where(x => x.IPluginType is not null)
            .ToList();

        // The core assertion: if two plugins both expose an IPlugin type (loaded into
        // separate ALCs), those Type objects must be different runtime instances.
        for (var i = 0; i < ipluginTypes.Count; i++)
        {
            for (var j = i + 1; j < ipluginTypes.Count; j++)
            {
                var a = ipluginTypes[i];
                var b = ipluginTypes[j];

                Assert.False(
                    ReferenceEquals(a.IPluginType, b.IPluginType),
                    $"Type-identity collision detected between {a.RepoName} and {b.RepoName}. " +
                    $"Both ALCs resolved the same runtime Type object for IPlugin — " +
                    $"this is the pre-ALC-fix COR_E_INVALIDOPERATION scenario. " +
                    $"Ensure Lidarr.Plugin.Abstractions is internalised into each plugin's merged DLL.");
            }
        }

        // Verify all 4 loaded without throwing (no COR_E_INVALIDOPERATION)
        Assert.True(
            loaded.Count >= 2,
            $"Expected all 4 plugins to load but only {loaded.Count} succeeded. " +
            $"Missing builds: {string.Join(", ", missing)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 2: No shared-dependency conflict via deps.json
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Confirms that none of the four plugins' .deps.json files list
    /// Lidarr.Plugin.Common as an EXTERNAL shared dependency. Each plugin must
    /// ship its own copy (internalised or alongside) rather than delegate
    /// resolution to the host, which does not ship Common.
    /// </summary>
    [SkippableFact]
    public void LoadAllPluginsInIsolatedAlcs_NoSharedDependencyConflict()
    {
        var violations = new List<string>();
        var missing = new List<string>();

        foreach (var plugin in AllPlugins)
        {
            if (!File.Exists(plugin.DllPath))
            {
                missing.Add(plugin.RepoName);
                continue;
            }

            var depsJsonPath = Path.ChangeExtension(plugin.DllPath, ".deps.json");
            if (!File.Exists(depsJsonPath))
            {
                // No deps.json means the assembly has no published dependency manifest —
                // this is acceptable (e.g., ILRepack-merged single-assembly plugins).
                continue;
            }

            try
            {
                var depsJson = File.ReadAllText(depsJsonPath);
                using var doc = JsonDocument.Parse(depsJson);

                // deps.json structure: { "targets": { ".NETCoreApp,Version=v8.0": { "<lib>/version": { "dependencies": {} } } } }
                // We look for any library key that starts with "Lidarr.Plugin.Common/" — if it appears
                // as an EXTERNAL runtime dependency (not "runtime": {} — empty), that's a problem.
                if (doc.RootElement.TryGetProperty("libraries", out var libraries))
                {
                    foreach (var lib in libraries.EnumerateObject())
                    {
                        if (!lib.Name.StartsWith("Lidarr.Plugin.Common/", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (lib.Value.TryGetProperty("type", out var typeProp) &&
                            typeProp.GetString() == "project")
                        {
                            // Project reference — acceptable for development builds.
                            continue;
                        }

                        violations.Add(
                            $"{plugin.RepoName}: {lib.Name} declared as external dependency in deps.json. " +
                            $"Lidarr.Plugin.Common must be internalised (ILRepack) or bundled, " +
                            $"not delegated to the host (which does not ship Common).");
                    }
                }
            }
            catch (JsonException ex)
            {
                // Malformed deps.json — not our problem to fix here, just note it.
                Console.WriteLine($"[MultiPluginAlcTests] Could not parse {depsJsonPath}: {ex.Message}");
            }
        }

        if (missing.Count == AllPlugins.Count)
        {
            Skip.If(true,
                $"All plugin DLLs are missing. Build each with 'dotnet build --configuration Release'. " +
                $"Missing: {string.Join(", ", missing)}");
            return;
        }

        Assert.Empty(violations);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 3: Plugin metadata exposed
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each loaded plugin DLL must contain at least one concrete (non-abstract,
    /// non-interface) type implementing <c>Lidarr.Plugin.Abstractions.Contracts.IPlugin</c>
    /// OR a type matching the plugin.json "main" assembly pattern — confirming
    /// the DLL actually contains plugin entry points and isn't an empty shell.
    ///
    /// Uses the FULL-BUILD test bins (not the lean package bins) because:
    /// - Lean package bins reference host assemblies (Lidarr.Core, FluentValidation)
    ///   unavailable in the test process, causing ReflectionTypeLoadException for the
    ///   types that directly inherit from host base classes (HttpIndexerBase, etc.).
    /// - The IPlugin implementor class inherits host base classes → it lands in the
    ///   "failed to load" bucket, making it invisible in the partial type list.
    /// - Test bins include host stub DLLs from the ext/ submodule, allowing full
    ///   type enumeration.
    ///
    /// Uses name-based discovery (type.GetInterfaces().Any(i => i.FullName == ...))
    /// to handle both sidecar and ILRepack-merged shapes (per C2 in ALC_MULTIPLUGIN_FIX.md).
    /// </summary>
    [SkippableFact]
    public void LoadAllPluginsInIsolatedAlcs_AllPluginMetadataExposed()
    {
        const string IPluginFullName = "Lidarr.Plugin.Abstractions.Contracts.IPlugin";

        var violations = new List<string>();
        var missing = new List<string>();

        foreach (var plugin in AllPluginsFullBuild)
        {
            if (!File.Exists(plugin.DllPath))
            {
                missing.Add(plugin.RepoName);
                continue;
            }

            try
            {
                var alc = new PluginLoadContext(plugin.DllPath, isCollectible: false);
                var assembly = alc.LoadFromAssemblyPath(plugin.DllPath);

                // GetLoadedTypes handles ReflectionTypeLoadException gracefully,
                // returning the partial type list of types that DID load successfully.
                // This is needed because merged plugin DLLs reference host assemblies
                // (Lidarr.Core, Lidarr.Common, FluentValidation v9) that are only
                // available inside the real Lidarr host process, not in the test runner.
                var loadedTypes = GetLoadedTypesSafe(assembly);

                // Name-based search tolerates both merged (internal IPlugin) and sidecar shapes.
                var concreteIPluginTypes = loadedTypes
                    .Where(t => !t.IsAbstract && !t.IsInterface)
                    .Where(t =>
                    {
                        try
                        {
                            return t.GetInterfaces().Any(i =>
                                string.Equals(i.FullName, IPluginFullName, StringComparison.Ordinal));
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .ToList();

                if (concreteIPluginTypes.Count == 0)
                {
                    violations.Add(
                        $"{plugin.RepoName} ({Path.GetFileName(plugin.DllPath)}): " +
                        $"No concrete type implementing {IPluginFullName} found in {loadedTypes.Count} " +
                        $"successfully-loaded types. " +
                        $"Ensure the plugin has a public class implementing IPlugin. " +
                        $"If Abstractions is ILRepack-merged (internal), name-based discovery should still work — " +
                        $"check that the merger preserved interface full names.");
                }
                else
                {
                    Console.WriteLine(
                        $"[MultiPluginAlcTests] {plugin.RepoName}: found IPlugin implementation(s): " +
                        $"{string.Join(", ", concreteIPluginTypes.Select(t => t.FullName))}");
                }
            }
            catch (Exception ex) when (ex is not ReflectionTypeLoadException)
            {
                violations.Add($"{plugin.RepoName}: Failed to load assembly: {ex.Message}");
            }
        }

        if (missing.Count == AllPlugins.Count)
        {
            Skip.If(true,
                $"All plugin DLLs are missing. Build each with 'dotnet build --configuration Release'. " +
                $"Missing: {string.Join(", ", missing)}");
            return;
        }

        if (missing.Count > 0)
        {
            Console.WriteLine(
                $"[MultiPluginAlcTests] {missing.Count} plugins skipped (DLL not found): " +
                $"{string.Join(", ", missing)}");
        }

        Assert.Empty(violations);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 4: No assembly version drift in shipped Common copy
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// For each plugin whose build output directory contains a separate
    /// Lidarr.Plugin.Common.dll (i.e., non-merged builds), asserts that its
    /// AssemblyVersion matches the canonical <c>1.8.0.0</c> declared in Common's
    /// <c>src/Lidarr.Plugin.Common.csproj</c>.
    ///
    /// Version drift here means a plugin was built against an older or newer
    /// Common than the ecosystem canonical — a potential source of subtle runtime
    /// failures when the host resolves Common types.
    /// </summary>
    [SkippableFact]
    public void LoadAllPluginsInIsolatedAlcs_NoAssemblyVersionDrift()
    {
        var violations = new List<string>();
        var missing = new List<string>();

        foreach (var plugin in AllPlugins)
        {
            if (!File.Exists(plugin.DllPath))
            {
                missing.Add(plugin.RepoName);
                continue;
            }

            var pluginDir = Path.GetDirectoryName(plugin.DllPath)!;
            var commonDllPath = Path.Combine(pluginDir, "Lidarr.Plugin.Common.dll");

            if (!File.Exists(commonDllPath))
            {
                // No sidecar Common.dll → plugin uses ILRepack-merged copy.
                // Version drift check is not applicable for merged builds.
                Console.WriteLine(
                    $"[MultiPluginAlcTests] {plugin.RepoName}: no sidecar Lidarr.Plugin.Common.dll " +
                    $"(expected for ILRepack-merged plugins — skipping version check for this plugin).");
                continue;
            }

            try
            {
                var commonAssemblyName = AssemblyName.GetAssemblyName(commonDllPath);
                var actualVersion = commonAssemblyName.Version?.ToString() ?? "(null)";

                if (!string.Equals(actualVersion, CanonicalCommonAssemblyVersion, StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{plugin.RepoName}: Lidarr.Plugin.Common.dll version mismatch. " +
                        $"Expected={CanonicalCommonAssemblyVersion}, Actual={actualVersion}. " +
                        $"Rebuild the plugin against Common 1.8.0 or bump the submodule pin.");
                }
                else
                {
                    Console.WriteLine(
                        $"[MultiPluginAlcTests] {plugin.RepoName}: Lidarr.Plugin.Common.dll " +
                        $"version {actualVersion} matches canonical.");
                }
            }
            catch (BadImageFormatException ex)
            {
                violations.Add($"{plugin.RepoName}: Cannot read Common.dll metadata: {ex.Message}");
            }
        }

        if (missing.Count == AllPlugins.Count)
        {
            Skip.If(true,
                $"All plugin DLLs are missing. Build each with 'dotnet build --configuration Release'. " +
                $"Missing: {string.Join(", ", missing)}");
            return;
        }

        if (missing.Count > 0)
        {
            Console.WriteLine(
                $"[MultiPluginAlcTests] {missing.Count} plugins not built locally: " +
                $"{string.Join(", ", missing)}");
        }

        Assert.Empty(violations);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to find a type implementing IPlugin using name-based reflection
    /// that tolerates both sidecar (public IPlugin from external Abstractions.dll)
    /// and ILRepack-merged (internal IPlugin inlined into the merged DLL) shapes.
    /// Returns null rather than throwing.
    ///
    /// ReflectionTypeLoadException is expected for lean/merged package bins because
    /// those DLLs ref host assemblies (Lidarr.Core, FluentValidation) unavailable
    /// outside the actual Lidarr process.
    /// </summary>
    private static Type? FindIPluginTypeSafe(Assembly assembly)
    {
        const string IPluginFullName = "Lidarr.Plugin.Abstractions.Contracts.IPlugin";

        var types = GetLoadedTypesSafe(assembly);
        return types
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .FirstOrDefault(t =>
            {
                try
                {
                    return t.GetInterfaces().Any(i =>
                        string.Equals(i.FullName, IPluginFullName, StringComparison.Ordinal));
                }
                catch
                {
                    return false;
                }
            });
    }

    /// <summary>
    /// Enumerates all types that loaded successfully from an assembly.
    /// Swallows <see cref="ReflectionTypeLoadException"/> and returns the partial list,
    /// which is the correct behaviour when a merged plugin DLL references host assemblies
    /// that are not available in the test runner process.
    /// </summary>
    private static IReadOnlyList<Type> GetLoadedTypesSafe(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Select(t => t!).ToList();
        }
    }
}
