using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Lidarr.Plugin.Common.TestKit.Packaging;

/// <summary>
/// Shared path-discovery helpers for plugin packaging policy tests.
/// Each plugin calls <see cref="For"/> once to bind a <see cref="PackagingTestPaths"/>
/// instance to its own plugin name and optional environment-variable override.
/// </summary>
public sealed class PackagingTestPaths
{
    private readonly string _pluginName;
    private readonly string _envVarName;

    private PackagingTestPaths(string pluginName, string envVarName)
    {
        _pluginName = pluginName;
        _envVarName = envVarName;
    }

    /// <summary>
    /// Creates a <see cref="PackagingTestPaths"/> bound to the given plugin.
    /// </summary>
    /// <param name="pluginName">
    /// Pascal-cased plugin name (e.g. "AppleMusicarr", "Brainarr", "Tidalarr", "Qobuzarr").
    /// Used both for zip glob matching (<c>{pluginName}-*.zip</c>) and — when
    /// <paramref name="envVarName"/> is omitted — to derive the default env var
    /// <c>{PLUGINNAME}_PACKAGE_PATH</c>.
    /// </param>
    /// <param name="envVarName">
    /// Override the derived env var name.  Defaults to
    /// <c>{pluginName.ToUpperInvariant()}_PACKAGE_PATH</c>.
    /// </param>
    public static PackagingTestPaths For(string pluginName, string? envVarName = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            throw new ArgumentException("pluginName must not be empty.", nameof(pluginName));
        }

        var resolvedEnvVar = string.IsNullOrWhiteSpace(envVarName)
            ? $"{pluginName.ToUpperInvariant()}_PACKAGE_PATH"
            : envVarName;

        return new PackagingTestPaths(pluginName, resolvedEnvVar);
    }

    /// <summary>
    /// Returns <see langword="true"/> when packaging tests should be treated as
    /// mandatory failures rather than skipped.  Triggered by:
    /// <list type="bullet">
    ///   <item><c>REQUIRE_PACKAGE_TESTS=1|true</c></item>
    ///   <item><c>CI=1|true</c> (Tidal superset behaviour, adopted for all plugins)</item>
    /// </list>
    /// </summary>
    public static bool IsStrictMode()
        => IsTruthy(Environment.GetEnvironmentVariable("CI"))
        || IsTruthy(Environment.GetEnvironmentVariable("REQUIRE_PACKAGE_TESTS"));

    /// <summary>
    /// Tries to locate the built plugin zip.  Checks, in order:
    /// <list type="number">
    ///   <item><c>PLUGIN_PACKAGE_PATH</c> (legacy generic override)</item>
    ///   <item>Plugin-specific env var (e.g. <c>APPLEMUSICARR_PACKAGE_PATH</c>)</item>
    ///   <item>Repo root and <c>artifacts/packages/</c> sub-directory, newest zip first.</item>
    /// </list>
    /// </summary>
    public string? TryFindPackagePath()
    {
        var explicitPath =
            Environment.GetEnvironmentVariable("PLUGIN_PACKAGE_PATH") ??
            Environment.GetEnvironmentVariable(_envVarName);

        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var repoRoot = TryFindRepoRoot();
        if (repoRoot is null)
        {
            return null;
        }

        var searchPaths = new[]
        {
            repoRoot,
            Path.Combine(repoRoot, "artifacts", "packages"),
        };

        var candidates = searchPaths
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.GetFiles(dir, $"{_pluginName}-*.zip")
                .Concat(Directory.GetFiles(dir, $"{_pluginName}-*.net8.0.zip"))
                .Concat(Directory.GetFiles(dir, $"{_pluginName}-latest.zip")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        return candidates.FirstOrDefault()?.FullName;
    }

    /// <summary>
    /// Returns the package path, throwing if it cannot be found.
    /// </summary>
    public string RequirePackagePath()
        => TryFindPackagePath()
        ?? throw new InvalidOperationException(
            $"{_pluginName} package not found. "
            + $"Build the package or set {_envVarName} to the zip path.");

    /// <summary>
    /// Opens the located package zip for reading (auto-resolves the path).
    /// </summary>
    public ZipArchive OpenPackageZip()
        => ZipFile.OpenRead(RequirePackagePath());

    /// <summary>
    /// Opens the package zip at the given explicit path for reading.
    /// Convenience overload for call sites that already hold a path string.
    /// </summary>
    public static ZipArchive OpenPackageZip(string packagePath)
        => ZipFile.OpenRead(packagePath);

    /// <summary>
    /// Returns the repo root, throwing if it cannot be found.
    /// </summary>
    public string FindRepoRootOrThrow()
        => TryFindRepoRoot()
        ?? throw new InvalidOperationException(
            $"Failed to locate {_pluginName} repo root.");

    /// <summary>
    /// Returns the path to the packaging policy baseline markdown file, or
    /// <see langword="null"/> if neither the repo root nor the docs directory can be found.
    /// </summary>
    public string? TryFindPackagingPolicyBaselinePath()
    {
        var repoRoot = TryFindRepoRoot();
        if (repoRoot is null)
        {
            return null;
        }

        var path = Path.Combine(repoRoot, "docs", "PACKAGING_POLICY_BASELINE.md");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Walks upward from the test assembly directory looking for a
    /// <c>{PluginName}.sln</c> sentinel file.
    /// </summary>
    public string? TryFindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, $"{_pluginName}.sln")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
