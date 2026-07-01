using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.PackageValidation;

public class PluginPackageValidatorPolicyTests
{
    [Fact]
    public void RequiredAndTypeIdentityAssemblies_DoNotContradictForbiddenPackageContents()
    {
        var forbidden = ReadForbiddenPackageContents();
        var requiredByValidator = PluginPackageValidator.RequiredPluginAssemblies
            .Concat(PluginPackageValidator.TypeIdentityAssemblies)
            .ToArray();

        var contradictions = requiredByValidator
            .Where(a => forbidden.Contains(a, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            contradictions.Length == 0,
            $"PluginPackageValidator requires package sidecars forbidden by parity-spec.json: {string.Join(", ", contradictions)}");
    }

    [Fact]
    public void DisallowedAssemblies_CoverForbiddenPackageContents()
    {
        var forbidden = ReadForbiddenPackageContents();

        var missingFromValidator = forbidden
            .Except(PluginPackageValidator.DisallowedHostAssemblies, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            missingFromValidator.Length == 0,
            $"PluginPackageValidator does not reject forbidden package contents from parity-spec.json: {string.Join(", ", missingFromValidator)}");
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
}
