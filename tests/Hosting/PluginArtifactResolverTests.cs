using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Lidarr.Plugin.Common.TestKit.Hosting;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Hosting;

public sealed class PluginArtifactResolverTests
{
    [Fact]
    public void FindPluginDll_PrefersPackagedPublishArtifact_OverRawBinOutput()
    {
        using var repo = TemporaryRepo.Create();
        string publishDll = repo.Touch("artifacts", "publish", "net8.0", "Release", "Lidarr.Plugin.Test.dll");
        repo.Touch("bin", "Lidarr.Plugin.Test.dll");

        string? resolved = PluginArtifactResolver.FindPluginDll(
            repo.Root,
            "Lidarr.Plugin.Test.dll",
            Path.Combine("bin", "Lidarr.Plugin.Test.dll"));

        Assert.Equal(publishDll, resolved);
    }

    [Fact]
    public void FindPluginDll_SkipsCandidate_WhenForbiddenHostBoundarySidecarIsPresent()
    {
        using var repo = TemporaryRepo.Create();
        repo.Touch("bin", "Lidarr.Plugin.Test.dll");
        repo.Touch("bin", "FluentValidation.dll");

        string? resolved = PluginArtifactResolver.FindPluginDll(
            repo.Root,
            "Lidarr.Plugin.Test.dll",
            Path.Combine("bin", "Lidarr.Plugin.Test.dll"));

        Assert.Null(resolved);
    }

    [Fact]
    public void FindPluginDll_FailsClosed_WhenPreferredPackageArtifactHasForbiddenSidecar_EvenIfRawFallbackExists()
    {
        using var repo = TemporaryRepo.Create();
        repo.Touch("artifacts", "publish", "net8.0", "Release", "Lidarr.Plugin.Test.dll");
        repo.Touch("artifacts", "publish", "net8.0", "Release", "FluentValidation.dll");
        repo.Touch("bin", "Release", "Lidarr.Plugin.Test.dll");

        string? resolved = PluginArtifactResolver.FindPluginDll(
            repo.Root,
            "Lidarr.Plugin.Test.dll",
            Path.Combine("bin", "Release", "Lidarr.Plugin.Test.dll"));

        Assert.Null(resolved);
    }

    [Fact]
    public void FindPluginDll_FallsBackToRawCandidate_WhenPackageArtifactsAreAbsent()
    {
        using var repo = TemporaryRepo.Create();
        string rawDll = repo.Touch("bin", "Release", "Lidarr.Plugin.Test.dll");

        string? resolved = PluginArtifactResolver.FindPluginDll(
            repo.Root,
            "Lidarr.Plugin.Test.dll",
            Path.Combine("bin", "Lidarr.Plugin.Test.dll"),
            Path.Combine("bin", "Release", "Lidarr.Plugin.Test.dll"));

        Assert.Equal(rawDll, resolved);
    }

    [Fact]
    public void ForbiddenHostBoundarySidecars_IncludesParityForbiddenPackageContents()
    {
        var paritySpecPath = Path.Combine(FindRepoRoot(), "scripts", "parity-spec.json");
        using var document = JsonDocument.Parse(File.ReadAllText(paritySpecPath));
        var forbiddenPackageContents = document.RootElement
            .GetProperty("versionContract")
            .GetProperty("forbiddenPackageContents")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        foreach (var forbiddenDll in forbiddenPackageContents)
        {
            Assert.Contains(forbiddenDll!, PluginArtifactResolver.ForbiddenHostBoundarySidecars);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "parity-spec.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to find repository root containing scripts/parity-spec.json.");
    }

    private sealed class TemporaryRepo : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lpc-artifacts-" + Guid.NewGuid().ToString("N"));

        private TemporaryRepo()
        {
            Directory.CreateDirectory(Root);
        }

        public static TemporaryRepo Create() => new();

        public string Touch(params string[] parts)
        {
            string path = Path.Combine(new[] { Root }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "placeholder");
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; temp files are safe to leave behind if locked.
            }
        }
    }
}
