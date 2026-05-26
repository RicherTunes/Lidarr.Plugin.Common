using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.PackageClosure;

/// <summary>
/// Validates that each plugin's packaged closure (build output directory or
/// packaged ZIP) does not contain any assembly listed in
/// <c>scripts/parity-spec.json → versionContract.forbiddenPackageContents</c>.
///
/// Forbidden assemblies are host-provided DLLs that cause type-identity conflicts
/// when shipped inside a plugin package. The parity-spec is the single source of
/// truth for this list.
///
/// Tests SKIP (rather than FAIL) when a plugin's build output is not present.
/// Build the plugin with "dotnet build --configuration Release" first.
/// </summary>
public sealed class PackageClosureTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Static fixture — loaded once per test run
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Forbidden DLLs loaded from parity-spec.json at startup.
    /// Falls back to a hardcoded list when the spec file cannot be read
    /// (e.g., when the test assembly is loaded outside the repo tree).
    /// </summary>
    private static readonly IReadOnlyList<string> ForbiddenPackageContents =
        LoadForbiddenPackageContents();

    /// <summary>
    /// Maps repo names to their canonical plugin package output directories.
    /// The test uses the FIRST directory that exists and contains the main plugin DLL.
    /// Directories are listed most-specific → least-specific so the best build wins.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, PluginPackageSpec> PluginSpecs =
        new Dictionary<string, PluginPackageSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["brainarr"] = new PluginPackageSpec(
                MainDllName: "Lidarr.Plugin.Brainarr.dll",
                CandidateDirs:
                [
                    @"C:\R\Alex\github\brainarr\Brainarr.Plugin\bin",
                    @"C:\R\Alex\github\brainarr\_plugins\Brainarr.Plugin",
                    @"C:\R\Alex\github\brainarr\Brainarr.Tests\bin\Release\net8.0",
                ]),

            ["qobuzarr"] = new PluginPackageSpec(
                MainDllName: "Lidarr.Plugin.Qobuzarr.dll",
                CandidateDirs:
                [
                    @"C:\R\Alex\github\qobuzarr\bin",
                    @"C:\R\Alex\github\qobuzarr\plugin-dist",
                    @"C:\R\Alex\github\qobuzarr\tests\Qobuzarr.Tests\bin\Release\net8.0",
                ]),

            ["tidalarr"] = new PluginPackageSpec(
                MainDllName: "Lidarr.Plugin.Tidalarr.dll",
                CandidateDirs:
                [
                    @"C:\R\Alex\github\tidalarr\src\Tidalarr\bin",
                    @"C:\R\Alex\github\tidalarr\tests\Tidalarr.Tests\bin\Release\net8.0",
                ]),

            ["applemusicarr"] = new PluginPackageSpec(
                MainDllName: "AppleMusicarr.Plugin.dll",
                CandidateDirs:
                [
                    @"C:\R\Alex\github\applemusicarr\src\AppleMusicarr.Plugin\bin\Release\net8.0",
                    @"C:\R\Alex\github\applemusicarr\src\AppleMusicarr.Cli\bin\Release\net8.0",
                ]),
        };

    // ─────────────────────────────────────────────────────────────────────────
    // Deliverable B — Main theory: forbidden assembly check
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// For each plugin, enumerates all DLLs in its canonical build output directory
    /// (or packaged ZIP) and asserts that none match any entry in
    /// <c>parity-spec.json → versionContract.forbiddenPackageContents</c>.
    ///
    /// Finding a forbidden DLL here is a HIGH-SEVERITY violation: it means the plugin
    /// ships a host-provided assembly that will cause type-identity conflicts when two
    /// or more plugins are installed simultaneously (the COR_E_INVALIDOPERATION scenario).
    /// </summary>
    [SkippableTheory]
    [InlineData("brainarr")]
    [InlineData("qobuzarr")]
    [InlineData("tidalarr")]
    [InlineData("applemusicarr")]
    public void PluginPackage_ContainsNoForbiddenAssemblies(string repoName)
    {
        var (packageDir, dllsInPackage) = ResolvePackageContents(repoName);

        if (packageDir is null)
        {
            Skip.If(true,
                $"Build output for '{repoName}' not found. " +
                $"Run 'dotnet build --configuration Release' in C:\\R\\Alex\\github\\{repoName} " +
                $"before running this suite.");
            return;
        }

        var foundForbidden = dllsInPackage
            .Where(dll => ForbiddenPackageContents.Any(forbidden =>
                string.Equals(Path.GetFileName(dll), forbidden, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (foundForbidden.Count > 0)
        {
            var report = string.Join(Environment.NewLine,
                foundForbidden.Select(f =>
                    $"  FORBIDDEN: {Path.GetFileName(f)} (in {Path.GetDirectoryName(f)})"));

            Assert.Fail(
                $"[PackageClosure] HIGH SEVERITY: Plugin '{repoName}' ships forbidden assemblies " +
                $"in package directory '{packageDir}'.{Environment.NewLine}" +
                $"These DLLs are host-provided and cause COR_E_INVALIDOPERATION type-identity conflicts " +
                $"when multiple plugins are installed:{Environment.NewLine}{report}{Environment.NewLine}" +
                $"Fix: remove from PluginPackaging.targets _PluginRuntimeDeps or strip via ILRepack. " +
                $"See docs/dev-guide/ALC_MULTIPLUGIN_FIX.md for the full explanation.");
        }

        // Success: emit the clean manifest for traceability in CI output.
        Console.WriteLine(
            $"[PackageClosure] {repoName} OK — {dllsInPackage.Count} DLLs in '{packageDir}', " +
            $"none forbidden. Assemblies: {string.Join(", ", dllsInPackage.Select(Path.GetFileName))}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Deliverable B — Positive check: required files present
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that each plugin's canonical build output contains the main plugin DLL
    /// and a plugin.json manifest — the minimum set required by the Lidarr plugin loader.
    /// </summary>
    [SkippableTheory]
    [InlineData("brainarr")]
    [InlineData("qobuzarr")]
    [InlineData("tidalarr")]
    [InlineData("applemusicarr")]
    public void PluginPackage_ContainsRequiredFiles(string repoName)
    {
        if (!PluginSpecs.TryGetValue(repoName, out var spec))
        {
            Assert.Fail($"No PluginPackageSpec registered for '{repoName}'. Update {nameof(PackageClosureTests)}.");
            return;
        }

        var packageDir = spec.CandidateDirs
            .FirstOrDefault(d => File.Exists(Path.Combine(d, spec.MainDllName)));

        if (packageDir is null)
        {
            Skip.If(true,
                $"Build output for '{repoName}' not found. " +
                $"Run 'dotnet build --configuration Release' in C:\\R\\Alex\\github\\{repoName} " +
                $"before running this suite.");
            return;
        }

        // The main plugin DLL must be present.
        Assert.True(
            File.Exists(Path.Combine(packageDir, spec.MainDllName)),
            $"{repoName}: Main plugin DLL '{spec.MainDllName}' not found in '{packageDir}'.");

        // plugin.json must be present.
        var pluginJsonPath = Path.Combine(packageDir, "plugin.json");
        Assert.True(
            File.Exists(pluginJsonPath),
            $"{repoName}: 'plugin.json' not found in '{packageDir}'. " +
            $"The Lidarr host requires plugin.json to discover the plugin.");

        // plugin.json must parse and contain required fields.
        if (File.Exists(pluginJsonPath))
        {
            AssertPluginJsonValid(repoName, pluginJsonPath);
        }

        Console.WriteLine($"[PackageClosure] {repoName}: required files present in '{packageDir}'.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Deliverable B — Full enumeration report
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits a full inventory of each plugin's package contents — top-level DLLs only,
    /// classified as ALLOWED, FORBIDDEN, or REQUIRED. Useful for auditing package bloat.
    /// Always passes; intended as a diagnostic / CI log artefact.
    /// </summary>
    [SkippableTheory]
    [InlineData("brainarr")]
    [InlineData("qobuzarr")]
    [InlineData("tidalarr")]
    [InlineData("applemusicarr")]
    public void PluginPackage_FullInventoryReport(string repoName)
    {
        var (packageDir, dllsInPackage) = ResolvePackageContents(repoName);

        if (packageDir is null)
        {
            Skip.If(true,
                $"Build output for '{repoName}' not found. " +
                $"Run 'dotnet build --configuration Release' in C:\\R\\Alex\\github\\{repoName}.");
            return;
        }

        Console.WriteLine($"[PackageClosure/Inventory] {repoName} — package dir: {packageDir}");
        Console.WriteLine($"  Total top-level DLLs: {dllsInPackage.Count}");

        foreach (var dll in dllsInPackage.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dll);
            var category = ForbiddenPackageContents.Any(f =>
                string.Equals(f, name, StringComparison.OrdinalIgnoreCase))
                ? "FORBIDDEN"
                : "OK";

            Console.WriteLine($"  [{category,-8}] {name}");
        }

        // This test always passes — it's a diagnostic report.
        Assert.True(true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static (string? PackageDir, IReadOnlyList<string> Dlls) ResolvePackageContents(string repoName)
    {
        if (!PluginSpecs.TryGetValue(repoName, out var spec))
        {
            return (null, []);
        }

        // Check for a pre-built ZIP first (artifacts/*.zip pattern).
        var repoRoot = Path.Combine(@"C:\R\Alex\github", repoName);
        var zipPath = FindPluginZip(repoRoot, repoName);
        if (zipPath is not null)
        {
            var dlls = EnumerateDllsInZip(zipPath);
            return (zipPath, dlls);
        }

        // Fall back to build output directories.
        var bestDir = spec.CandidateDirs
            .FirstOrDefault(d => File.Exists(Path.Combine(d, spec.MainDllName)));

        if (bestDir is null)
        {
            return (null, []);
        }

        // Enumerate top-level DLLs only (not runtimes/ subdirs) to match what gets loaded by Lidarr.
        var topLevelDlls = Directory.GetFiles(bestDir, "*.dll", SearchOption.TopDirectoryOnly)
            .ToList();

        return (bestDir, topLevelDlls);
    }

    private static string? FindPluginZip(string repoRoot, string repoName)
    {
        var artifactDirs = new[] { "artifacts", "dist", "output" };
        foreach (var dir in artifactDirs)
        {
            var artifactsPath = Path.Combine(repoRoot, dir);
            if (!Directory.Exists(artifactsPath))
            {
                continue;
            }

            var zips = Directory.GetFiles(artifactsPath, "*.zip", SearchOption.TopDirectoryOnly);
            if (zips.Length > 0)
            {
                return zips.OrderByDescending(File.GetLastWriteTime).First();
            }
        }

        return null;
    }

    private static IReadOnlyList<string> EnumerateDllsInZip(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries
                .Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Where(e => !e.FullName.Contains('/') || e.FullName.Split('/').Length == 2)
                .Select(e => e.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PackageClosure] Could not read ZIP {zipPath}: {ex.Message}");
            return [];
        }
    }

    private static void AssertPluginJsonValid(string repoName, string pluginJsonPath)
    {
        try
        {
            var json = File.ReadAllText(pluginJsonPath);
            using var doc = JsonDocument.Parse(json);

            // Required fields per parity-spec.json → pluginJson.requiredFields
            var requiredFields = new[] { "id", "name", "version", "main", "commonVersion" };
            foreach (var field in requiredFields)
            {
                Assert.True(
                    doc.RootElement.TryGetProperty(field, out _),
                    $"{repoName}: plugin.json missing required field '{field}' in '{pluginJsonPath}'.");
            }
        }
        catch (JsonException ex)
        {
            Assert.Fail($"{repoName}: plugin.json is not valid JSON: {ex.Message}");
        }
    }

    private static IReadOnlyList<string> LoadForbiddenPackageContents()
    {
        // Hardcoded fallback (mirrors parity-spec.json → versionContract.forbiddenPackageContents)
        var fallback = new[]
        {
            "FluentValidation.dll",
            "NLog.dll",
            "System.Text.Json.dll",
            "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
            "Microsoft.Extensions.Logging.Abstractions.dll",
            "Microsoft.Extensions.Logging.dll",
            "Microsoft.Extensions.Configuration.dll",
            "Microsoft.Extensions.Caching.Memory.dll",
            "Microsoft.Extensions.Http.dll",
            "Lidarr.Core.dll",
            "Lidarr.Common.dll",
            "Lidarr.Http.dll",
            "Lidarr.Api.V1.dll",
            "NzbDrone.Core.dll",
            "NzbDrone.Common.dll",
        };

        // Try to load from the live parity-spec.json in the repo tree.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "parity-spec.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "parity-spec.json"),
            @"C:\R\Alex\github\lidarr.plugin.common\scripts\parity-spec.json",
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (!File.Exists(full))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(full);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement
                        .TryGetProperty("versionContract", out var vc) &&
                    vc.TryGetProperty("forbiddenPackageContents", out var list))
                {
                    var items = list.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)
                        .ToList();

                    if (items.Count > 0)
                    {
                        Console.WriteLine(
                            $"[PackageClosure] Loaded {items.Count} forbidden package entries from {full}");
                        return items;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PackageClosure] Could not load parity-spec.json from {full}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[PackageClosure] Using hardcoded fallback forbidden list ({fallback.Length} entries). " +
            $"parity-spec.json could not be located from test execution directory.");
        return fallback;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Type definitions
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record PluginPackageSpec(
        string MainDllName,
        IReadOnlyList<string> CandidateDirs);
}
