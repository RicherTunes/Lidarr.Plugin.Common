using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Compliance;

public sealed class CanonicalHostPinRepositoryTests
{
    private const string CanonicalNLogVersion = "5.4.0";

    private static readonly Dictionary<string, string> CanonicalHostPins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Extensions.DependencyInjection"] = "8.0.1",
        ["Microsoft.Extensions.Logging"] = "8.0.1",
        ["Microsoft.Extensions.Logging.Abstractions"] = "8.0.3",
        ["Microsoft.Extensions.Http"] = "8.0.1",
        ["FluentValidation"] = "9.5.4",
        ["NLog"] = CanonicalNLogVersion,
    };

    [Fact]
    public void LockFiles_PinNLogToCanonicalHostVersion()
    {
        var repoRoot = FindRepoRoot();
        var lockFiles = new[]
        {
            Path.Combine(repoRoot, "testkit", "packages.lock.json"),
            Path.Combine(repoRoot, "tests", "packages.lock.json"),
        };

        var offenders = new List<string>();
        foreach (var lockFile in lockFiles)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(lockFile));
            CollectNLogLockDrift(
                doc.RootElement,
                Path.GetRelativePath(repoRoot, lockFile).Replace('\\', '/'),
                "$",
                offenders);
        }

        Assert.True(
            offenders.Count == 0,
            "NLog is host-coupled and lock files must not retain a stale resolved/requested version:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void PluginProjectTemplate_UsesCanonicalHostCoupledPackagePins()
    {
        var repoRoot = FindRepoRoot();
        var templatePath = Path.Combine(repoRoot, "templates", "plugin-project", "Directory.Packages.props.template");
        var doc = XDocument.Load(templatePath);

        var actualPins = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageVersion")
            .Select(e => new
            {
                Id = (string?)e.Attribute("Include"),
                Version = (string?)e.Attribute("Version"),
            })
            .Where(p => p.Id is not null && CanonicalHostPins.ContainsKey(p.Id))
            .ToDictionary(p => p.Id!, p => p.Version ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var offenders = new List<string>();
        foreach (var (id, expected) in CanonicalHostPins)
        {
            if (actualPins.TryGetValue(id, out var actual) && !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                offenders.Add($"{id} = {actual} (expected {expected})");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The legacy plugin-project template must not teach new plugins unsafe host-boundary pins:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void MultiPluginAlcGuide_DocumentsNLogHostPin()
    {
        var repoRoot = FindRepoRoot();
        var guide = File.ReadAllText(Path.Combine(repoRoot, "docs", "dev-guide", "ALC_MULTIPLUGIN_FIX.md"));

        Assert.Contains("| `NLog v5.", guide, StringComparison.Ordinal);
        Assert.Contains("| `v5.4.0", guide, StringComparison.Ordinal);
        Assert.Contains("Pin plugin to 5.4.0", guide, StringComparison.Ordinal);
    }

    private static void CollectNLogLockDrift(JsonElement element, string relativePath, string jsonPath, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = $"{jsonPath}.{property.Name}";
                    if (string.Equals(property.Name, "NLog", StringComparison.OrdinalIgnoreCase))
                    {
                        ValidateNLogLockEntry(property.Value, relativePath, childPath, offenders);
                    }

                    CollectNLogLockDrift(property.Value, relativePath, childPath, offenders);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CollectNLogLockDrift(item, relativePath, $"{jsonPath}[{index++}]", offenders);
                }

                break;
        }
    }

    private static void ValidateNLogLockEntry(JsonElement value, string relativePath, string jsonPath, List<string> offenders)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("requested", out var requested)
                && requested.ValueKind == JsonValueKind.String
                && !string.Equals(requested.GetString(), $"[{CanonicalNLogVersion}, )", StringComparison.Ordinal))
            {
                offenders.Add($"{relativePath}:{jsonPath}.requested = {requested.GetString()}");
            }

            if (value.TryGetProperty("resolved", out var resolved)
                && resolved.ValueKind == JsonValueKind.String
                && !string.Equals(resolved.GetString(), CanonicalNLogVersion, StringComparison.Ordinal))
            {
                offenders.Add($"{relativePath}:{jsonPath}.resolved = {resolved.GetString()}");
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var actual = value.GetString();
            if (!string.Equals(actual, $"[{CanonicalNLogVersion}, )", StringComparison.Ordinal)
                && !string.Equals(actual, CanonicalNLogVersion, StringComparison.Ordinal))
            {
                offenders.Add($"{relativePath}:{jsonPath} = {actual}");
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && dir is not null; depth++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "lidarr.plugin.common.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            "Could not locate repo root (lidarr.plugin.common.sln) from " + AppContext.BaseDirectory);
    }
}
