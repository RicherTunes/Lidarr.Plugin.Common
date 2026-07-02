using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.PackageClosure;

public sealed class PackageClosureValidatorPolicyTests
{
    [Fact]
    public void ValidateDirectory_AllowsMainPluginAssemblyByDefault()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Lidarr.Plugin.Test.dll"), "plugin");

            var result = PackageClosureValidator.ValidateDirectory(tempDir);

            Assert.True(result.IsValid, result.GetSummary());
            Assert.DoesNotContain("Lidarr.Plugin.Test.dll", result.Violations, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Theory]
    [InlineData("Lidarr.Plugin.Common.dll")]
    [InlineData("Lidarr.Plugin.Abstractions.dll")]
    public void ValidateDirectory_RejectsMergedPolicySidecarsByDefault(string sidecarName)
    {
        var tempDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Lidarr.Plugin.Test.dll"), "plugin");
            File.WriteAllText(Path.Combine(tempDir, sidecarName), "sidecar");

            var result = PackageClosureValidator.ValidateDirectory(tempDir);

            Assert.False(result.IsValid);
            Assert.Contains(sidecarName, result.Violations, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Theory]
    [InlineData("Lidarr.Plugin.Common.dll")]
    [InlineData("Lidarr.Plugin.Abstractions.dll")]
    public void ValidatePackage_RejectsMergedPolicySidecarsByDefault(string sidecarName)
    {
        var tempDir = CreateTempDirectory();
        var zipPath = Path.Combine(tempDir, "plugin.zip");
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                archive.CreateEntry("Lidarr.Plugin.Test.dll");
                archive.CreateEntry(sidecarName);
            }

            var result = PackageClosureValidator.ValidatePackage(zipPath);

            Assert.False(result.IsValid);
            Assert.Contains(sidecarName, result.Violations, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void DefaultDisallowedAssemblies_CoverParitySpecForbiddenPackageContents()
    {
        var forbidden = ReadForbiddenPackageContents();
        var missingFromValidator = forbidden
            .Except(PackageClosureValidator.DefaultDisallowedAssemblies, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            missingFromValidator.Length == 0,
            $"PackageClosureValidator does not reject forbidden package contents from parity-spec.json: {string.Join(", ", missingFromValidator)}");
    }

    [Fact]
    public void DefaultAllowedAssemblies_DoNotContradictParitySpecForbiddenPackageContents()
    {
        var forbidden = ReadForbiddenPackageContents();
        var contradictions = PackageClosureValidator.DefaultAllowedAssemblies
            .Where(a => forbidden.Contains(a, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            contradictions.Length == 0,
            $"PackageClosureValidator allows package sidecars forbidden by parity-spec.json: {string.Join(", ", contradictions)}");
    }

    private static string[] ReadForbiddenPackageContents()
    {
        var root = FindRepoRoot();
        var specPath = Path.Combine(root, "scripts", "parity-spec.json");
        using var document = JsonDocument.Parse(File.ReadAllText(specPath));
        return document.RootElement
            .GetProperty("versionContract")
            .GetProperty("forbiddenPackageContents")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "scripts", "parity-spec.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to find repository root containing scripts/parity-spec.json.");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pkg_closure_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
