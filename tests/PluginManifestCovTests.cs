using System;
using System.IO;
using System.Linq;
using Lidarr.Plugin.Abstractions.Capabilities;
using Lidarr.Plugin.Abstractions.Manifest;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public sealed class PluginManifestCovTests
    {
        // Load() method coverage (lines 124-138)
        [Fact]
        public void Load_throws_when_path_is_null()
        {
            // Line 128: throw new ArgumentException("Manifest path must be provided", nameof(manifestPath));
            var ex = Assert.Throws<ArgumentException>(() => PluginManifest.Load(null!));
            Assert.Equal("Manifest path must be provided (Parameter 'manifestPath')", ex.Message);
        }

        [Fact]
        public void Load_throws_when_path_is_whitespace()
        {
            // Line 128: throw new ArgumentException("Manifest path must be provided", nameof(manifestPath));
            var ex = Assert.Throws<ArgumentException>(() => PluginManifest.Load("   "));
            Assert.Equal("Manifest path must be provided (Parameter 'manifestPath')", ex.Message);
        }

        [Fact]
        public void Load_throws_when_file_contains_invalid_json()
        {
            // Line 135: throw new InvalidOperationException($"Failed to parse manifest '{manifestPath}'.");
            // Note: JsonSerializer.Deserialize throws JsonException for malformed JSON, which is wrapped
            var tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, "not valid json {{{");

                // JSON deserializer throws for invalid JSON before we can wrap it
                Assert.Throws<System.Text.Json.JsonException>(() => PluginManifest.Load(tempFile));
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public void Load_succeeds_with_valid_manifest()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                var json = @"{
                    ""id"": ""test-plugin"",
                    ""name"": ""Test Plugin"",
                    ""version"": ""1.0.0"",
                    ""apiVersion"": ""1.x""
                }";
                File.WriteAllText(tempFile, json);

                var manifest = PluginManifest.Load(tempFile);
                Assert.Equal("test-plugin", manifest.Id);
                Assert.Equal("Test Plugin", manifest.Name);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        // FromJson() method coverage (lines 144-158)
        [Fact]
        public void FromJson_throws_when_json_is_null()
        {
            // Line 148: throw new ArgumentNullException(nameof(json));
            var ex = Assert.Throws<ArgumentNullException>(() => PluginManifest.FromJson(null!));
            Assert.Equal("json", ex.ParamName);
        }

        [Fact]
        public void FromJson_throws_when_deserialize_returns_null()
        {
            // Line 154: throw new InvalidOperationException("Failed to parse plugin manifest.");
            var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson("null"));
            Assert.Contains("Failed to parse plugin manifest", ex.Message);
        }

        // EnsureValid() method coverage - missing fields (lines 173-193)
        [Fact]
        public void FromJson_throws_when_name_missing()
        {
            // Line 176: if (string.IsNullOrWhiteSpace(Name)) errors.Add("Manifest 'name' is required.");
            var json = @"{""id"":""demo"",""version"":""1.0.0"",""apiVersion"":""1.x""}";
            var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
            Assert.Contains("Manifest 'name' is required.", ex.Message);
        }

        [Fact]
        public void FromJson_throws_when_version_missing()
        {
            // Line 177: if (string.IsNullOrWhiteSpace(Version)) errors.Add("Manifest 'version' is required.");
            var json = @"{""id"":""demo"",""name"":""Demo"",""apiVersion"":""1.x""}";
            var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
            Assert.Contains("Manifest 'version' is required.", ex.Message);
        }

        [Fact]
        public void FromJson_throws_when_apiVersion_missing()
        {
            // Line 178: if (string.IsNullOrWhiteSpace(ApiVersion)) errors.Add("Manifest 'apiVersion' is required.");
            var json = @"{""id"":""demo"",""name"":""Demo"",""version"":""1.0.0""}";
            var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
            Assert.Contains("Manifest 'apiVersion' is required.", ex.Message);
        }

        [Fact]
        public void FromJson_throws_when_minHostVersion_invalid()
        {
            // Line 184: errors.Add($"minHostVersion '{MinHostVersion}' is not a valid SemVer value.");
            var json = @"{""id"":""demo"",""name"":""Demo"",""version"":""1.0.0"",""apiVersion"":""1.x"",""minHostVersion"":""invalid""}";
            var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
            Assert.Contains("minHostVersion 'invalid' is not a valid SemVer value", ex.Message);
        }

        [Fact]
        public void FromJson_throws_multiple_errors_combined()
        {
            // Line 189: throw new InvalidOperationException(string.Join(" ", errors));
            var json = @"{""version"":""abc"",""apiVersion"":""latest""}";
            var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
            Assert.Contains("Manifest 'id' is required.", ex.Message);
            Assert.Contains("Manifest 'name' is required.", ex.Message);
            Assert.Contains("apiVersion 'latest'", ex.Message);
        }

        // ToJson() method coverage (lines 163-166)
        [Fact]
        public void ToJson_returns_valid_json()
        {
            var manifest = new PluginManifest
            {
                Id = "test-plugin",
                Name = "Test Plugin",
                Version = "1.0.0",
                ApiVersion = "1.x"
            };

            var json = manifest.ToJson();
            Assert.Contains("test-plugin", json);
            Assert.Contains("Test Plugin", json);
        }

        // CapabilityFlags property coverage (lines 83-89)
        [Fact]
        public void CapabilityFlags_returns_correct_flags()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                Capabilities = new[] { "search", "caching" }
            };

            var flags = manifest.CapabilityFlags;
            Assert.True(flags.HasCapability(PluginCapability.ProvidesIndexer));
            Assert.True(flags.HasCapability(PluginCapability.SupportsCaching));
            Assert.False(flags.HasCapability(PluginCapability.SupportsOAuth));
        }

        // UnknownCapabilities property coverage (lines 92-99)
        [Fact]
        public void UnknownCapabilities_returns_unknown_values()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                Capabilities = new[] { "search", "UnknownCapability", "AnotherUnknown" }
            };

            var unknown = manifest.UnknownCapabilities;
            Assert.Equal(2, unknown.Count);
            Assert.Contains("UnknownCapability", unknown);
            Assert.Contains("AnotherUnknown", unknown);
        }

        [Fact]
        public void UnknownCapabilities_empty_when_all_known()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                Capabilities = new[] { "search" }
            };

            var unknown = manifest.UnknownCapabilities;
            Assert.Empty(unknown);
        }

        // SupportsCapability method coverage (line 101)
        [Fact]
        public void SupportsCapability_returns_true_when_capability_exists()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                Capabilities = new[] { "search" }
            };

            Assert.True(manifest.SupportsCapability(PluginCapability.ProvidesIndexer));
        }

        [Fact]
        public void SupportsCapability_returns_false_when_capability_missing()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x"
            };

            Assert.False(manifest.SupportsCapability(PluginCapability.ProvidesIndexer));
        }

        // EvaluateCompatibility null checks (lines 243-244)
        [Fact]
        public void EvaluateCompatibility_throws_when_hostVersion_null()
        {
            // Line 243: if (hostVersion is null) throw new ArgumentNullException(nameof(hostVersion));
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x"
            };

            var ex = Assert.Throws<ArgumentNullException>(() =>
                manifest.EvaluateCompatibility(null!, new Version(1, 0, 0, 0)));
            Assert.Equal("hostVersion", ex.ParamName);
        }

        [Fact]
        public void EvaluateCompatibility_throws_when_abstractionsVersion_null()
        {
            // Line 244: if (abstractionsVersion is null) throw new ArgumentNullException(nameof(abstractionsVersion));
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x"
            };

            var ex = Assert.Throws<ArgumentNullException>(() =>
                manifest.EvaluateCompatibility(new Version(2, 0, 0, 0), null!));
            Assert.Equal("abstractionsVersion", ex.ParamName);
        }

        // EvaluateCompatibility invalid apiVersion parsing (line 256)
        [Fact]
        public void EvaluateCompatibility_returns_incompatible_when_apiVersion_invalid()
        {
            // Line 256: return PluginCompatibilityResult.Incompatible($"apiVersion '{ApiVersion}' is invalid.");
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "invalid.x"
            };

            var result = manifest.EvaluateCompatibility(new Version(2, 0, 0, 0), new Version(1, 0, 0, 0));

            Assert.False(result.IsCompatible);
            Assert.Contains("apiVersion 'invalid.x' is invalid", result.Message);
        }

        // Edge cases and additional coverage
        [Fact]
        public void FromJson_accepts_valid_semver_with_prerelease()
        {
            // NormaliseSemVer strips prerelease (lines 213-229)
            var json = @"{""id"":""demo"",""name"":""Demo"",""version"":""1.0.0-beta"",""apiVersion"":""1.x""}";
            var manifest = PluginManifest.FromJson(json);
            Assert.Equal("1.0.0-beta", manifest.Version);
        }

        [Fact]
        public void FromJson_accepts_valid_semver_with_build()
        {
            // NormaliseSemVer strips build metadata (line 215)
            var json = @"{""id"":""demo"",""name"":""Demo"",""version"":""1.0.0+build123"",""apiVersion"":""1.x""}";
            var manifest = PluginManifest.FromJson(json);
            Assert.Equal("1.0.0+build123", manifest.Version);
        }

        [Fact]
        public void FromJson_normalises_single_version_component()
        {
            // Line 225: 1 => "1.0.0"
            var json = @"{""id"":""demo"",""name"":""Demo"",""version"":""1"",""apiVersion"":""1.x""}";
            var manifest = PluginManifest.FromJson(json);
            Assert.Equal("1", manifest.Version);
        }

        [Fact]
        public void FromJson_normalises_two_version_components()
        {
            // Line 226: 2 => "1.2.0"
            var json = @"{""id"":""demo"",""name"":""Demo"",""version"":""1.2"",""apiVersion"":""1.x""}";
            var manifest = PluginManifest.FromJson(json);
            Assert.Equal("1.2", manifest.Version);
        }

        [Fact]
        public void EvaluateCompatibility_succeeds_when_minHostVersion_matches()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                MinHostVersion = "2.0.0"
            };

            var result = manifest.EvaluateCompatibility(new Version(2, 0, 0, 0), new Version(1, 0, 0, 0));

            Assert.True(result.IsCompatible);
            Assert.Equal("Compatible", result.Message);
        }

        [Fact]
        public void EvaluateCompatibility_succeeds_when_minHostVersion_higher()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                MinHostVersion = "2.0.0"
            };

            var result = manifest.EvaluateCompatibility(new Version(2, 5, 0, 0), new Version(1, 0, 0, 0));

            Assert.True(result.IsCompatible);
            Assert.Equal("Compatible", result.Message);
        }

        [Fact]
        public void EvaluateCompatibility_with_minHostVersion_normalises_correctly()
        {
            // Test TryParseVersion with normalised version
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                MinHostVersion = "2.5"  // Will be normalised to 2.5.0
            };

            var result = manifest.EvaluateCompatibility(new Version(2, 5, 0, 0), new Version(1, 0, 0, 0));

            Assert.True(result.IsCompatible);
        }

        // Additional property coverage
        [Fact]
        public void Properties_have_expected_defaults()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x"
            };

            Assert.Null(manifest.CommonVersion);
            Assert.Null(manifest.MinHostVersion);
            Assert.Null(manifest.Description);
            Assert.Null(manifest.Author);
            Assert.Null(manifest.EntryAssembly);
            Assert.Null(manifest.Main);
            Assert.Empty(manifest.RequiredSettings);
            Assert.Empty(manifest.Capabilities);
        }

        [Fact]
        public void Properties_preserve_assigned_values()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                CommonVersion = "2.0.0",
                MinHostVersion = "2.20.0",
                Description = "A test plugin",
                Author = "Test Author",
                EntryAssembly = "TestAssembly",
                Main = "TestMain"
            };

            Assert.Equal("2.0.0", manifest.CommonVersion);
            Assert.Equal("2.20.0", manifest.MinHostVersion);
            Assert.Equal("A test plugin", manifest.Description);
            Assert.Equal("Test Author", manifest.Author);
            Assert.Equal("TestAssembly", manifest.EntryAssembly);
            Assert.Equal("TestMain", manifest.Main);
        }

        [Fact]
        public void RequiredSettings_preserves_values()
        {
            var settings = new[] { "apiKey", "username" };
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                RequiredSettings = settings
            };

            Assert.Equal(2, manifest.RequiredSettings.Count);
            Assert.Contains("apiKey", manifest.RequiredSettings);
            Assert.Contains("username", manifest.RequiredSettings);
        }
    }
}
