using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace Tidalarr.Tests;

public class PackagingTests
{
    [Trait("scope","cli")] // Only runs in packaging/CLI scenarios
    [Fact]
    public void DependencyClosure_Excludes_HostAssemblies()
    {
        var repoRoot = GetRepoRoot();
        var packagesDir = Path.Combine(repoRoot, "src", "Tidalarr", "artifacts", "packages");
        if (!Directory.Exists(packagesDir))
        {
            throw new SkipException($"Packages directory not found: {packagesDir}. Run packaging first.");
        }

        var zip = Directory.EnumerateFiles(packagesDir, "*.zip", SearchOption.TopDirectoryOnly)
                           .OrderByDescending(File.GetLastWriteTimeUtc)
                           .FirstOrDefault();
        if (zip is null)
        {
            throw new SkipException("No plugin package zip found. Run packaging to generate an artifact.");
        }

        using var archive = ZipFile.OpenRead(zip);
        var dlls = archive.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                  .Select(e => e.Name)
                                  .ToArray();

        // Fail if any host assemblies or Abstractions leak into the zip
        string[] forbiddenPrefixes = new[]
        {
            "Lidarr.Core", "Lidarr.Common", // Host assemblies
            "NzbDrone.", "Sonarr.", "Radarr.", // Other host identities pattern
        };

        var offenders = dlls.Where(name =>
            forbiddenPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(name, "Lidarr.Plugin.Abstractions.dll", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(offenders.Length == 0, $"Package contains forbidden assemblies: {string.Join(", ", offenders)}");
    }

    private static string GetRepoRoot()
    {
        // Walk up from current test directory to repository root (contains Tidalarr.sln)
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            var sln = Path.Combine(dir, "Tidalarr.sln");
            if (File.Exists(sln)) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        // Fallback: use current working directory
        return Directory.GetCurrentDirectory();
    }

    private sealed class SkipException : Xunit.Sdk.XunitException
    {
        public SkipException(string message) : base(message) { }
    }
}

