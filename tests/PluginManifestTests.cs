using System;
using Lidarr.Plugin.Abstractions.Manifest;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class PluginManifestTests
    {
        [Fact]
        public void FromJson_throws_when_required_fields_missing()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson("{}"));
            Assert.Contains("Manifest 'id' is required.", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FromJson_throws_when_version_invalid()
        {
            var json = "{\"id\":\"demo\",\"name\":\"Demo\",\"version\":\"abc\",\"apiVersion\":\"1.x\"}";
            var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
            Assert.Contains("Manifest version 'abc'", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FromJson_throws_when_api_version_invalid()
        {
            var json = "{\"id\":\"demo\",\"name\":\"Demo\",\"version\":\"1.0.0\",\"apiVersion\":\"latest\"}";
            var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromJson(json));
            Assert.Contains("apiVersion 'latest'", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EvaluateCompatibility_enforces_min_host_version()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                MinHostVersion = "2.20.0"
            };

            var result = manifest.EvaluateCompatibility(new Version(2, 12, 0, 0), new Version(1, 0, 0, 0));

            Assert.False(result.IsCompatible);
            Assert.Contains("Host version", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EvaluateCompatibility_enforces_abstractions_major_match()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "2.x"
            };

            var result = manifest.EvaluateCompatibility(new Version(2, 12, 0, 0), new Version(1, 0, 0, 0));

            Assert.False(result.IsCompatible);
            Assert.Contains("abstractions major", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EvaluateCompatibility_succeeds_for_matching_versions()
        {
            var manifest = new PluginManifest
            {
                Id = "demo",
                Name = "Demo",
                Version = "1.0.0",
                ApiVersion = "1.x",
                MinHostVersion = "2.10.0"
            };

            var result = manifest.EvaluateCompatibility(new Version(2, 12, 0, 0), new Version(1, 0, 0, 0));

            Assert.True(result.IsCompatible);
            Assert.Equal("Compatible", result.Message);
        }
    }
}

