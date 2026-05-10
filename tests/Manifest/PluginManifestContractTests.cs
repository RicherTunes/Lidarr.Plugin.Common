using System;
using System.Text.Json;
using Lidarr.Plugin.Abstractions.Capabilities;
using Lidarr.Plugin.Abstractions.Manifest;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Manifest;

[Trait("Category", "Contract")]
public class PluginManifestContractTests
{
    private const string ValidManifestJson = """
        {
            "id": "test-plugin",
            "name": "Test Plugin",
            "version": "1.2.3",
            "apiVersion": "1.x",
            "commonVersion": "1.7.1",
            "minHostVersion": "2.14.0",
            "description": "A test plugin",
            "author": "Test Author",
            "capabilities": ["search", "download"],
            "requiredSettings": ["ConfigPath", "ApiKey"],
            "main": "Lidarr.Plugin.Test.dll"
        }
        """;

    #region Deserialization Contract

    [Fact]
    public void FromJson_ValidManifest_DeserializesAllFields()
    {
        var manifest = PluginManifest.FromJson(ValidManifestJson);

        Assert.Equal("test-plugin", manifest.Id);
        Assert.Equal("Test Plugin", manifest.Name);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("1.x", manifest.ApiVersion);
        Assert.Equal("1.7.1", manifest.CommonVersion);
        Assert.Equal("2.14.0", manifest.MinHostVersion);
        Assert.Equal("A test plugin", manifest.Description);
        Assert.Equal("Test Author", manifest.Author);
        Assert.Equal("Lidarr.Plugin.Test.dll", manifest.Main);
        Assert.Contains("ConfigPath", manifest.RequiredSettings);
        Assert.Contains("ApiKey", manifest.RequiredSettings);
        Assert.Equal(2, manifest.RequiredSettings.Count);
    }

    [Fact]
    public void FromJson_RoundTrip_PreservesAllFields()
    {
        var original = PluginManifest.FromJson(ValidManifestJson);
        var json = original.ToJson();
        var roundTripped = PluginManifest.FromJson(json);

        Assert.Equal(original.Id, roundTripped.Id);
        Assert.Equal(original.Name, roundTripped.Name);
        Assert.Equal(original.Version, roundTripped.Version);
        Assert.Equal(original.ApiVersion, roundTripped.ApiVersion);
        Assert.Equal(original.CommonVersion, roundTripped.CommonVersion);
        Assert.Equal(original.MinHostVersion, roundTripped.MinHostVersion);
        Assert.Equal(original.Main, roundTripped.Main);
        Assert.Equal(original.RequiredSettings.Count, roundTripped.RequiredSettings.Count);
        Assert.Equal(original.Capabilities.Count, roundTripped.Capabilities.Count);
    }

    [Fact]
    public void FromJson_WithComments_ParsesSuccessfully()
    {
        var json = """
            {
                // This is a comment
                "id": "commented",
                "name": "Commented Plugin",
                "version": "1.0.0",
                "apiVersion": "1.x"
            }
            """;

        var manifest = PluginManifest.FromJson(json);
        Assert.Equal("commented", manifest.Id);
    }

    [Fact]
    public void FromJson_WithTrailingCommas_ParsesSuccessfully()
    {
        var json = """
            {
                "id": "trailing",
                "name": "Trailing Plugin",
                "version": "1.0.0",
                "apiVersion": "1.x",
            }
            """;

        var manifest = PluginManifest.FromJson(json);
        Assert.Equal("trailing", manifest.Id);
    }

    [Fact]
    public void FromJson_CaseInsensitivePropertyNames()
    {
        var json = """
            {
                "Id": "case-test",
                "Name": "Case Test",
                "Version": "1.0.0",
                "ApiVersion": "1.x"
            }
            """;

        var manifest = PluginManifest.FromJson(json);
        Assert.Equal("case-test", manifest.Id);
    }

    #endregion

    #region Validation Contract

    [Fact]
    public void FromJson_MissingId_ThrowsInvalidOperation()
    {
        var json = """{ "name": "No ID", "version": "1.0.0", "apiVersion": "1.x" }""";
        var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
        Assert.Contains("'id' is required", ex.Message);
    }

    [Fact]
    public void FromJson_MissingVersion_ThrowsInvalidOperation()
    {
        var json = """{ "id": "test", "name": "No Version", "apiVersion": "1.x" }""";
        var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
        Assert.Contains("'version' is required", ex.Message);
    }

    [Fact]
    public void FromJson_InvalidApiVersion_ThrowsInvalidOperation()
    {
        var json = """{ "id": "test", "name": "Bad API", "version": "1.0.0", "apiVersion": "1.0" }""";
        var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
        Assert.Contains("'major.x' format", ex.Message);
    }

    [Fact]
    public void FromJson_InvalidSemVer_ThrowsInvalidOperation()
    {
        var json = """{ "id": "test", "name": "Bad Ver", "version": "not.a.version", "apiVersion": "1.x" }""";
        var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
        Assert.Contains("valid SemVer", ex.Message);
    }

    [Fact]
    public void FromJson_NullJson_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => PluginManifest.FromJson(null!));
    }

    #endregion

    #region Capability Contract

    [Fact]
    public void Capabilities_ParsedToFlags()
    {
        var manifest = PluginManifest.FromJson(ValidManifestJson);

        Assert.True(manifest.SupportsCapability(PluginCapability.ProvidesIndexer));
        Assert.True(manifest.SupportsCapability(PluginCapability.ProvidesDownloadClient));
        Assert.False(manifest.SupportsCapability(PluginCapability.SupportsPlaylists));
    }

    [Fact]
    public void UnknownCapabilities_ReportsUnrecognizedValues()
    {
        var json = """
            {
                "id": "cap-test",
                "name": "Cap Test",
                "version": "1.0.0",
                "apiVersion": "1.x",
                "capabilities": ["search", "FutureFeature"]
            }
            """;

        var manifest = PluginManifest.FromJson(json);
        Assert.Contains("FutureFeature", manifest.UnknownCapabilities);
        Assert.True(manifest.SupportsCapability(PluginCapability.ProvidesIndexer));
    }

    #endregion

    #region Compatibility Contract

    [Fact]
    public void EvaluateCompatibility_HostMeetsMinVersion_ReturnsCompatible()
    {
        var manifest = PluginManifest.FromJson(ValidManifestJson);
        var result = manifest.EvaluateCompatibility(new Version(2, 15, 0), new Version(1, 0, 0));

        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void EvaluateCompatibility_HostBelowMinVersion_ReturnsIncompatible()
    {
        var manifest = PluginManifest.FromJson(ValidManifestJson);
        var result = manifest.EvaluateCompatibility(new Version(2, 13, 0), new Version(1, 0, 0));

        Assert.False(result.IsCompatible);
        Assert.Contains("lower than required", result.Message);
    }

    [Fact]
    public void EvaluateCompatibility_AbstractionsMajorMismatch_ReturnsIncompatible()
    {
        var manifest = PluginManifest.FromJson(ValidManifestJson);
        var result = manifest.EvaluateCompatibility(new Version(3, 0, 0), new Version(2, 0, 0));

        Assert.False(result.IsCompatible);
        Assert.Contains("major", result.Message);
    }

    [Fact]
    public void EvaluateCompatibility_NoMinHostVersion_AlwaysCompatible()
    {
        var json = """{ "id": "test", "name": "No Min", "version": "1.0.0", "apiVersion": "1.x" }""";
        var manifest = PluginManifest.FromJson(json);
        var result = manifest.EvaluateCompatibility(new Version(1, 0, 0), new Version(1, 0, 0));

        Assert.True(result.IsCompatible);
    }

    #endregion

    #region SemVer Normalisation

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.2.3")]
    [InlineData("1.0.0-beta")]
    [InlineData("2.0.0-rc.1+build.123")]
    public void FromJson_VariousSemVerFormats_Accepted(string version)
    {
        var json = $$"""{ "id": "test", "name": "Test", "version": "{{version}}", "apiVersion": "1.x" }""";
        var manifest = PluginManifest.FromJson(json);
        Assert.Equal(version, manifest.Version);
    }

    #endregion

    #region Real Plugin Manifests

    [Theory]
    [InlineData("tidalarr", "Tidalarr", "1.x")]
    [InlineData("qobuzarr", "Qobuzarr", "1.x")]
    [InlineData("brainarr", "Brainarr", "1.x")]
    [InlineData("applemusicarr", "AppleMusicarr", "1.x")]
    public void RealPluginManifest_ParsesAndValidates(string id, string name, string apiVersion)
    {
        var pluginJsonPath = FindPluginJson(id);
        if (pluginJsonPath == null)
        {
            return; // Skip if not found (not all repos may be present)
        }

        var manifest = PluginManifest.Load(pluginJsonPath);
        Assert.Equal(id, manifest.Id);
        Assert.Equal(name, manifest.Name);
        Assert.Equal(apiVersion, manifest.ApiVersion);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Version));
    }

    private static string? FindPluginJson(string pluginId)
    {
        // Search upward from test output to find the repo root
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // Check if this is the common repo with sibling plugin repos
            var sibling = System.IO.Path.Combine(dir.FullName, pluginId, "plugin.json");
            if (System.IO.File.Exists(sibling)) return sibling;

            // Check parent
            var parent = dir.Parent;
            if (parent != null)
            {
                sibling = System.IO.Path.Combine(parent.FullName, pluginId, "plugin.json");
                if (System.IO.File.Exists(sibling)) return sibling;
            }

            dir = dir.Parent;
        }

        return null;
    }

    #endregion
}
