using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Abstract base class defining ecosystem parity tests that ALL Lidarr plugin repos must pass.
/// Plugin test projects should inherit from this class and implement the abstract properties.
/// Tests verify structural alignment with the canonical plugin template.
/// </summary>
/// <remarks>
/// To use:
/// 1. Create a test class that inherits from EcosystemParityTestBase
/// 2. Implement RepoRootPath, PluginId, PluginJsonPath
/// 3. Add [Trait("Category", "Parity")] to the inheritor class
/// 4. Run: dotnet test --filter "Category=Parity"
/// </remarks>
public abstract partial class EcosystemParityTestBase : IDisposable
{
    #region Abstract Properties

    /// <summary>
    /// Absolute path to the plugin repository root directory.
    /// </summary>
    protected abstract string RepoRootPath { get; }

    /// <summary>
    /// The plugin's unique identifier (e.g., "qobuzarr", "brainarr").
    /// </summary>
    protected abstract string PluginId { get; }

    /// <summary>
    /// Relative path from repo root to plugin.json (e.g., "src/Qobuzarr.Plugin/plugin.json").
    /// </summary>
    protected abstract string PluginJsonRelativePath { get; }

    #endregion

    #region Helper Methods

    private string RepoFile(string relativePath) =>
        Path.Combine(RepoRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private bool FileExists(string relativePath) =>
        File.Exists(RepoFile(relativePath));

    private string ReadFile(string relativePath) =>
        File.ReadAllText(RepoFile(relativePath));

    private XDocument LoadXml(string relativePath) =>
        XDocument.Parse(ReadFile(relativePath));

    private JsonElement LoadJson(string relativePath)
    {
        var text = ReadFile(relativePath);
        return JsonDocument.Parse(text).RootElement;
    }

    #endregion

    #region Directory.Build.props Tests

    public virtual ComplianceResult DirectoryBuildProps_Exists()
    {
        return FileExists("Directory.Build.props")
            ? ComplianceResult.Success
            : ComplianceResult.Failure("Directory.Build.props not found in repo root");
    }

    public virtual ComplianceResult DirectoryBuildProps_HasILRepackDisabled()
    {
        if (!FileExists("Directory.Build.props"))
            return ComplianceResult.Failure("Directory.Build.props not found");

        var doc = LoadXml("Directory.Build.props");
        var prop = doc.Descendants("ILRepackEnabled").FirstOrDefault();
        if (prop == null)
            return ComplianceResult.Failure("Missing <ILRepackEnabled> property");
        if (prop.Value != "false")
            return ComplianceResult.Failure($"ILRepackEnabled should be 'false', got '{prop.Value}'");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult DirectoryBuildProps_HasVersionManagement()
    {
        if (!FileExists("Directory.Build.props"))
            return ComplianceResult.Failure("Directory.Build.props not found");

        var content = ReadFile("Directory.Build.props");
        if (!content.Contains("VersionFromFile"))
            return ComplianceResult.Failure("Missing VERSION file integration (VersionFromFile property)");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult DirectoryBuildProps_HasSourceLink()
    {
        if (!FileExists("Directory.Build.props"))
            return ComplianceResult.Failure("Directory.Build.props not found");

        var content = ReadFile("Directory.Build.props");
        var errors = new List<string>();

        if (!content.Contains("PublishRepositoryUrl"))
            errors.Add("Missing <PublishRepositoryUrl>true</PublishRepositoryUrl>");
        if (!content.Contains("EmbedUntrackedSources"))
            errors.Add("Missing <EmbedUntrackedSources>true</EmbedUntrackedSources>");
        if (!content.Contains("Microsoft.SourceLink.GitHub"))
            errors.Add("Missing PackageReference for Microsoft.SourceLink.GitHub");

        return new ComplianceResult(errors.Count == 0, errors);
    }

    public virtual ComplianceResult DirectoryBuildProps_HasNoWarnSuppression()
    {
        if (!FileExists("Directory.Build.props"))
            return ComplianceResult.Failure("Directory.Build.props not found");

        var content = ReadFile("Directory.Build.props");
        if (!content.Contains("NoWarn"))
            return ComplianceResult.Failure("Missing <NoWarn> warning suppression section");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult DirectoryBuildProps_HasCPMExclusion()
    {
        if (!FileExists("Directory.Build.props"))
            return ComplianceResult.Failure("Directory.Build.props not found");

        var content = ReadFile("Directory.Build.props");
        if (!Regex.IsMatch(content, @"ManagePackageVersionsCentrally.*false"))
            return ComplianceResult.Failure("Missing CPM exclusion for ext/ submodule (ManagePackageVersionsCentrally=false condition)");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult DirectoryBuildProps_HasDeterministic()
    {
        if (!FileExists("Directory.Build.props"))
            return ComplianceResult.Failure("Directory.Build.props not found");

        var doc = LoadXml("Directory.Build.props");
        var prop = doc.Descendants("Deterministic").FirstOrDefault();
        if (prop == null || prop.Value != "true")
            return ComplianceResult.Failure("Missing <Deterministic>true</Deterministic>");

        return ComplianceResult.Success;
    }

    #endregion

    #region Directory.Packages.props Tests

    public virtual ComplianceResult DirectoryPackagesProps_Exists()
    {
        return FileExists("Directory.Packages.props")
            ? ComplianceResult.Success
            : ComplianceResult.Failure("Directory.Packages.props not found — Central Package Management required");
    }

    public virtual ComplianceResult DirectoryPackagesProps_EnablesCPM()
    {
        if (!FileExists("Directory.Packages.props"))
            return ComplianceResult.Failure("Directory.Packages.props not found");

        var doc = LoadXml("Directory.Packages.props");
        var prop = doc.Descendants("ManagePackageVersionsCentrally").FirstOrDefault();
        if (prop == null || prop.Value != "true")
            return ComplianceResult.Failure("Directory.Packages.props must have <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");

        return ComplianceResult.Success;
    }

    #endregion

    #region plugin.json Tests

    public virtual ComplianceResult PluginJson_HasAllRequiredFields()
    {
        if (!FileExists(PluginJsonRelativePath))
            return ComplianceResult.Failure($"plugin.json not found at {PluginJsonRelativePath}");

        var json = LoadJson(PluginJsonRelativePath);
        var requiredFields = new[]
        {
            "id", "apiVersion", "name", "version", "author", "description",
            "homepage", "license", "tags", "commonVersion", "minHostVersion",
            "targetFramework", "main", "rootNamespace"
        };

        var missing = requiredFields
            .Where(f => !json.TryGetProperty(f, out _))
            .ToList();

        if (missing.Count > 0)
            return ComplianceResult.Failure(
                $"plugin.json missing required fields: {string.Join(", ", missing)}");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult PluginJson_TargetFramework_IsNet8()
    {
        if (!FileExists(PluginJsonRelativePath))
            return ComplianceResult.Failure($"plugin.json not found at {PluginJsonRelativePath}");

        var json = LoadJson(PluginJsonRelativePath);
        if (!json.TryGetProperty("targetFramework", out var tf))
            return ComplianceResult.Failure("plugin.json missing 'targetFramework' field");

        if (tf.GetString() != "net8.0")
            return ComplianceResult.Failure($"plugin.json targetFramework should be 'net8.0', got '{tf.GetString()}'");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult PluginJson_HasCommonVersion()
    {
        if (!FileExists(PluginJsonRelativePath))
            return ComplianceResult.Failure($"plugin.json not found at {PluginJsonRelativePath}");

        var json = LoadJson(PluginJsonRelativePath);
        if (!json.TryGetProperty("commonVersion", out var cv))
            return ComplianceResult.Failure("plugin.json missing 'commonVersion' field");

        var value = cv.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return ComplianceResult.Failure("plugin.json 'commonVersion' must not be empty");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult PluginJson_HasAuthor()
    {
        if (!FileExists(PluginJsonRelativePath))
            return ComplianceResult.Failure($"plugin.json not found at {PluginJsonRelativePath}");

        var json = LoadJson(PluginJsonRelativePath);
        if (!json.TryGetProperty("author", out var author) || string.IsNullOrWhiteSpace(author.GetString()))
            return ComplianceResult.Failure("plugin.json missing or empty 'author' field");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult PluginJson_HasLicense()
    {
        if (!FileExists(PluginJsonRelativePath))
            return ComplianceResult.Failure($"plugin.json not found at {PluginJsonRelativePath}");

        var json = LoadJson(PluginJsonRelativePath);
        if (!json.TryGetProperty("license", out var license) || string.IsNullOrWhiteSpace(license.GetString()))
            return ComplianceResult.Failure("plugin.json missing or empty 'license' field");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult PluginJson_HasTags()
    {
        if (!FileExists(PluginJsonRelativePath))
            return ComplianceResult.Failure($"plugin.json not found at {PluginJsonRelativePath}");

        var json = LoadJson(PluginJsonRelativePath);
        if (!json.TryGetProperty("tags", out var tags))
            return ComplianceResult.Failure("plugin.json missing 'tags' field");

        if (tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() == 0)
            return ComplianceResult.Failure("plugin.json 'tags' must be a non-empty array");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult PluginJson_HasRootNamespace()
    {
        if (!FileExists(PluginJsonRelativePath))
            return ComplianceResult.Failure($"plugin.json not found at {PluginJsonRelativePath}");

        var json = LoadJson(PluginJsonRelativePath);
        if (!json.TryGetProperty("rootNamespace", out var ns) || string.IsNullOrWhiteSpace(ns.GetString()))
            return ComplianceResult.Failure("plugin.json missing or empty 'rootNamespace' field");

        return ComplianceResult.Success;
    }

    public virtual ComplianceResult PluginJson_NoNonStandardFields()
    {
        if (!FileExists(PluginJsonRelativePath))
            return ComplianceResult.Failure($"plugin.json not found at {PluginJsonRelativePath}");

        var json = LoadJson(PluginJsonRelativePath);
        var forbiddenFields = new Dictionary<string, string>
        {
            ["minimumVersion"] = "Use 'minHostVersion' instead (non-standard duplication)",
            ["targets"] = "Use 'targetFramework' string instead of 'targets' array"
        };

        var errors = new List<string>();
        foreach (var (field, reason) in forbiddenFields)
        {
            if (json.TryGetProperty(field, out _))
                errors.Add($"Non-standard field '{field}': {reason}");
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region manifest.json Tests

    public virtual ComplianceResult ManifestJson_TargetFramework_IsNet8()
    {
        var manifestPath = Path.Combine(
            Path.GetDirectoryName(RepoFile(PluginJsonRelativePath))!,
            "manifest.json");

        if (!File.Exists(manifestPath))
            return ComplianceResult.Success; // manifest.json is optional

        var text = File.ReadAllText(manifestPath);
        var json = JsonDocument.Parse(text).RootElement;

        var errors = new List<string>();

        // Check targetFrameworks array
        if (json.TryGetProperty("targetFrameworks", out var tfs) && tfs.ValueKind == JsonValueKind.Array)
        {
            var frameworks = tfs.EnumerateArray().Select(e => e.GetString()).ToList();
            if (frameworks.Contains("net6.0"))
                errors.Add("manifest.json contains 'net6.0' in targetFrameworks — must be 'net8.0'");
            if (!frameworks.Contains("net8.0"))
                errors.Add("manifest.json missing 'net8.0' in targetFrameworks");
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region global.json Tests

    public virtual ComplianceResult GlobalJson_Exists()
    {
        return FileExists("global.json")
            ? ComplianceResult.Success
            : ComplianceResult.Failure("global.json not found in repo root");
    }

    public virtual ComplianceResult GlobalJson_SdkVersion_Is8_0_100()
    {
        if (!FileExists("global.json"))
            return ComplianceResult.Failure("global.json not found");

        var json = LoadJson("global.json");
        var errors = new List<string>();

        if (!json.TryGetProperty("sdk", out var sdk))
        {
            return ComplianceResult.Failure("global.json missing 'sdk' section");
        }

        if (sdk.TryGetProperty("version", out var version))
        {
            if (version.GetString() != "8.0.100")
                errors.Add($"global.json sdk.version should be '8.0.100', got '{version.GetString()}'");
        }
        else
        {
            errors.Add("global.json sdk missing 'version' field");
        }

        if (sdk.TryGetProperty("rollForward", out var rollForward))
        {
            if (rollForward.GetString() != "latestFeature")
                errors.Add($"global.json sdk.rollForward should be 'latestFeature', got '{rollForward.GetString()}'");
        }
        else
        {
            errors.Add("global.json sdk missing 'rollForward' field");
        }

        return new ComplianceResult(errors.Count == 0, errors);
    }

    #endregion

    #region Aggregator

    /// <summary>
    /// Runs all ecosystem parity checks and returns aggregated results.
    /// </summary>
    public virtual ComplianceReport RunAllParityChecks()
    {
        var results = new Dictionary<string, ComplianceResult>
        {
            // Directory.Build.props
            [nameof(DirectoryBuildProps_Exists)] = DirectoryBuildProps_Exists(),
            [nameof(DirectoryBuildProps_HasILRepackDisabled)] = DirectoryBuildProps_HasILRepackDisabled(),
            [nameof(DirectoryBuildProps_HasVersionManagement)] = DirectoryBuildProps_HasVersionManagement(),
            [nameof(DirectoryBuildProps_HasSourceLink)] = DirectoryBuildProps_HasSourceLink(),
            [nameof(DirectoryBuildProps_HasNoWarnSuppression)] = DirectoryBuildProps_HasNoWarnSuppression(),
            [nameof(DirectoryBuildProps_HasCPMExclusion)] = DirectoryBuildProps_HasCPMExclusion(),
            [nameof(DirectoryBuildProps_HasDeterministic)] = DirectoryBuildProps_HasDeterministic(),

            // Directory.Packages.props
            [nameof(DirectoryPackagesProps_Exists)] = DirectoryPackagesProps_Exists(),
            [nameof(DirectoryPackagesProps_EnablesCPM)] = DirectoryPackagesProps_EnablesCPM(),

            // plugin.json
            [nameof(PluginJson_HasAllRequiredFields)] = PluginJson_HasAllRequiredFields(),
            [nameof(PluginJson_TargetFramework_IsNet8)] = PluginJson_TargetFramework_IsNet8(),
            [nameof(PluginJson_HasCommonVersion)] = PluginJson_HasCommonVersion(),
            [nameof(PluginJson_HasAuthor)] = PluginJson_HasAuthor(),
            [nameof(PluginJson_HasLicense)] = PluginJson_HasLicense(),
            [nameof(PluginJson_HasTags)] = PluginJson_HasTags(),
            [nameof(PluginJson_HasRootNamespace)] = PluginJson_HasRootNamespace(),
            [nameof(PluginJson_NoNonStandardFields)] = PluginJson_NoNonStandardFields(),

            // manifest.json
            [nameof(ManifestJson_TargetFramework_IsNet8)] = ManifestJson_TargetFramework_IsNet8(),

            // global.json
            [nameof(GlobalJson_Exists)] = GlobalJson_Exists(),
            [nameof(GlobalJson_SdkVersion_Is8_0_100)] = GlobalJson_SdkVersion_Is8_0_100(),
        };

        var passed = results.Values.Count(r => r.Passed);
        return new ComplianceReport(results, passed, results.Count);
    }

    #endregion

    public virtual void Dispose()
    {
        // Cleanup if needed
    }
}
