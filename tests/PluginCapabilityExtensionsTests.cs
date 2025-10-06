using System;
using System.Linq;
using Lidarr.Plugin.Abstractions.Capabilities;
using Lidarr.Plugin.Abstractions.Manifest;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class PluginCapabilityExtensionsTests
    {
        [Fact]
        public void ToManifestValues_ProducesStableOrdering()
        {
            var flags = PluginCapability.ProvidesIndexer | PluginCapability.SupportsAuthentication | PluginCapability.SupportsOAuth;

            var values = flags.ToManifestValues();

            Assert.Equal(new[] { "search", "authentication", "oauth" }, values);
        }

        [Fact]
        public void FromManifestValues_ReturnsFlagsAndUnknown()
        {
            var input = new[] { "download", "search-upc", "custom-cap" };

            var flags = PluginCapabilityExtensions.FromManifestValues(input, out var unknown);

            Assert.True(flags.HasCapability(PluginCapability.ProvidesDownloadClient));
            Assert.True(flags.HasCapability(PluginCapability.SupportsUpcSearch));
            Assert.Single(unknown);
            Assert.Equal("custom-cap", unknown.First());
        }

        [Fact]
        public void CapabilityFlags_AvailableOnManifest()
        {
            var manifest = new PluginManifest
            {
                Capabilities = new[] { "search", "download", "quality-selection" }
            };

            var flags = manifest.CapabilityFlags;

            Assert.True(flags.HasCapability(PluginCapability.ProvidesIndexer));
            Assert.True(flags.HasCapability(PluginCapability.ProvidesDownloadClient));
            Assert.True(flags.HasCapability(PluginCapability.SupportsQualitySelection));
            Assert.False(flags.HasCapability(PluginCapability.SupportsOAuth));
        }
    }
}
