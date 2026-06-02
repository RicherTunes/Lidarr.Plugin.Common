using System.Collections.Generic;
using Lidarr.Plugin.Common.Services.Drm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Covers the DRM download seam lifted into Common: <see cref="DrmTrack.IsDrmProtected"/>
    /// (a pure presence check on the PSSH init data — the only "logic" in the value model) and
    /// <see cref="ExternalDownloadHandlerLoader.TryLoad"/>'s default null-when-unconfigured path.
    /// Common ships no handler implementation (not even a mock), so an unconfigured loader must
    /// return <c>null</c> rather than throw.
    /// </summary>
    public sealed class DrmDownloadSeamTests
    {
        [Fact]
        public void IsDrmProtected_True_WhenPsshPresent()
        {
            var track = new DrmTrack { Pssh = "AAAAW3Bzc2gAAAAA..." };

            Assert.True(track.IsDrmProtected);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void IsDrmProtected_False_WhenPsshMissingOrWhitespace(string pssh)
        {
            var track = new DrmTrack { Pssh = pssh };

            Assert.False(track.IsDrmProtected);
        }

        [Fact]
        public void IsDrmProtected_False_ByDefault()
        {
            Assert.False(new DrmTrack().IsDrmProtected);
        }

        [Fact]
        public void Clone_IsDeep_CollectionsNotShared()
        {
            var original = new DrmTrack
            {
                Id = "id-1",
                Title = "Song",
                ManifestUrl = "https://example/manifest.mpd",
                Artists = new List<string> { "A" },
                ServiceHints = new Dictionary<string, string> { ["asin"] = "B000" }
            };

            var clone = (DrmTrack)original.Clone();
            clone.Artists.Add("B");
            clone.ServiceHints["region"] = "US";

            Assert.Equal("https://example/manifest.mpd", clone.ManifestUrl);
            Assert.Single(original.Artists);
            Assert.Single(original.ServiceHints);
        }

        [Fact]
        public void TryLoad_ReturnsNull_WhenNothingConfigured()
        {
            // No explicit args and no env-var names supplied: the normal default.
            var handler = ExternalDownloadHandlerLoader.TryLoad();

            Assert.Null(handler);
        }

        [Fact]
        public void TryLoad_ReturnsNull_WhenConfiguredEnvVarsAreUnset()
        {
            // Env-var names are provided but the variables themselves are not set in the
            // environment, so resolution yields nothing -> null (no handler).
            var handler = ExternalDownloadHandlerLoader.TryLoad(
                assemblyPathEnvVar: "BRAINARR_TEST_DRM_DL_PATH_UNSET",
                typeNameEnvVar: "BRAINARR_TEST_DRM_DL_TYPE_UNSET");

            Assert.Null(handler);
        }
    }
}
