using System;
using System.Collections.Generic;
using Lidarr.Plugin.Abstractions.Capabilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class PluginCapabilityExtensionsCovTests
    {
        // ── ToManifestValues ──────────────────────────────────────────

        [Fact]
        public void ToManifestValues_None_ReturnsEmptyList()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:56-59
            var result = PluginCapability.None.ToManifestValues();

            Assert.Empty(result);
        }

        [Fact]
        public void ToManifestValues_SingleFlag_ReturnsSingleValue()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:61-71
            var result = PluginCapability.SupportsOAuth.ToManifestValues();

            Assert.Single(result);
            Assert.Equal("oauth", result[0]);
        }

        [Fact]
        public void ToManifestValues_AllFlags_ReturnsAllManifestNames()
        {
            // There are 13 named capabilities in ManifestNames (lines 13-28)
            var all = PluginCapability.ProvidesIndexer
                | PluginCapability.ProvidesDownloadClient
                | PluginCapability.SupportsCaching
                | PluginCapability.SupportsAuthentication
                | PluginCapability.SupportsQualitySelection
                | PluginCapability.SupportsOAuth
                | PluginCapability.SupportsDeviceCode
                | PluginCapability.SupportsHiResAudio
                | PluginCapability.SupportsTrackPreviews
                | PluginCapability.SupportsBatchDownloads
                | PluginCapability.SupportsRegionalCatalogs
                | PluginCapability.SupportsIsrcSearch
                | PluginCapability.SupportsUpcSearch
                | PluginCapability.SupportsPlaylists;

            var result = all.ToManifestValues();

            Assert.Equal(14, result.Count);
        }

        // ── FromManifestValues ────────────────────────────────────────

        [Fact]
        public void FromManifestValues_Null_ThrowsArgumentNullException()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:80
            Assert.Throws<ArgumentNullException>(() =>
                PluginCapabilityExtensions.FromManifestValues(null!, out _));
        }

        [Fact]
        public void FromManifestValues_EmptyEnumerable_ReturnsNoneAndEmptyUnknown()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:83-104
            var flags = PluginCapabilityExtensions.FromManifestValues(
                Array.Empty<string>(), out var unknown);

            Assert.Equal(PluginCapability.None, flags);
            Assert.Empty(unknown);
        }

        [Fact]
        public void FromManifestValues_WhitespaceEntries_AreSkipped()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:88-91
            var input = new[] { "  ", "\t", "", "search" };

            var flags = PluginCapabilityExtensions.FromManifestValues(input, out var unknown);

            Assert.True(flags.HasCapability(PluginCapability.ProvidesIndexer));
            Assert.Empty(unknown);
        }

        [Fact]
        public void FromManifestValues_CaseInsensitiveMapping()
        {
            // NameToCapability uses StringComparer.OrdinalIgnoreCase (line 31)
            var input = new[] { "SEARCH", "Download", "QUALITY-SELECTION" };

            var flags = PluginCapabilityExtensions.FromManifestValues(input, out var unknown);

            Assert.True(flags.HasCapability(PluginCapability.ProvidesIndexer));
            Assert.True(flags.HasCapability(PluginCapability.ProvidesDownloadClient));
            Assert.True(flags.HasCapability(PluginCapability.SupportsQualitySelection));
            Assert.Empty(unknown);
        }

        [Fact]
        public void FromManifestValues_OnlyUnknown_ReturnsNoneWithAllUnknown()
        {
            var input = new[] { "nope", "also-nope" };

            var flags = PluginCapabilityExtensions.FromManifestValues(input, out var unknown);

            Assert.Equal(PluginCapability.None, flags);
            Assert.Equal(2, unknown.Count);
            Assert.Equal("nope", unknown[0]);
            Assert.Equal("also-nope", unknown[1]);
        }

        [Fact]
        public void FromManifestValues_WhitespaceInValue_TrimmedBeforeLookup()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:93
            var input = new[] { "  oauth  " };

            var flags = PluginCapabilityExtensions.FromManifestValues(input, out var unknown);

            Assert.True(flags.HasCapability(PluginCapability.SupportsOAuth));
            Assert.Empty(unknown);
        }

        // ── ToDisplayNames ────────────────────────────────────────────

        [Fact]
        public void ToDisplayNames_None_ReturnsEmptyList()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:112-115
            var result = PluginCapability.None.ToDisplayNames();

            Assert.Empty(result);
        }

        [Fact]
        public void ToDisplayNames_SingleFlag_ReturnsCorrectDisplayName()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:117-126
            var result = PluginCapability.SupportsHiResAudio.ToDisplayNames();

            Assert.Single(result);
            Assert.Equal("Hi-Res Audio", result[0]);
        }

        [Fact]
        public void ToDisplayNames_MultipleFlags_ReturnsOrderedDisplayNames()
        {
            // DisplayNames dictionary ordering (lines 33-49)
            var flags = PluginCapability.ProvidesDownloadClient
                | PluginCapability.SupportsOAuth;

            var result = flags.ToDisplayNames();

            Assert.Equal(2, result.Count);
            Assert.Equal("Download", result[0]);
            Assert.Equal("OAuth", result[1]);
        }

        [Fact]
        public void ToDisplayNames_EachFlag_HasHumanReadableName()
        {
            var all = PluginCapability.ProvidesIndexer
                | PluginCapability.ProvidesDownloadClient
                | PluginCapability.SupportsCaching
                | PluginCapability.SupportsAuthentication
                | PluginCapability.SupportsQualitySelection
                | PluginCapability.SupportsOAuth
                | PluginCapability.SupportsDeviceCode
                | PluginCapability.SupportsHiResAudio
                | PluginCapability.SupportsTrackPreviews
                | PluginCapability.SupportsBatchDownloads
                | PluginCapability.SupportsRegionalCatalogs
                | PluginCapability.SupportsIsrcSearch
                | PluginCapability.SupportsUpcSearch
                | PluginCapability.SupportsPlaylists;

            var result = all.ToDisplayNames();

            Assert.Equal(14, result.Count);
            Assert.Equal("Search", result[0]);
            Assert.Equal("Download", result[1]);
            Assert.Equal("Caching", result[2]);
            Assert.Equal("Authentication", result[3]);
            Assert.Equal("Quality Selection", result[4]);
            Assert.Equal("OAuth", result[5]);
            Assert.Equal("Device Code", result[6]);
            Assert.Equal("Hi-Res Audio", result[7]);
            Assert.Equal("Track Previews", result[8]);
            Assert.Equal("Batch Downloads", result[9]);
            Assert.Equal("Regional Catalogs", result[10]);
            Assert.Equal("Search by ISRC", result[11]);
            Assert.Equal("Search by UPC", result[12]);
            Assert.Equal("Playlists", result[13]);
        }

        // ── HasCapability ─────────────────────────────────────────────

        [Fact]
        public void HasCapability_WhenSet_ReturnsTrue()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:132
            var flags = PluginCapability.ProvidesIndexer | PluginCapability.SupportsOAuth;

            Assert.True(flags.HasCapability(PluginCapability.ProvidesIndexer));
        }

        [Fact]
        public void HasCapability_WhenNotSet_ReturnsFalse()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:132
            var flags = PluginCapability.ProvidesIndexer;

            Assert.False(flags.HasCapability(PluginCapability.SupportsOAuth));
        }

        [Fact]
        public void HasCapability_NoneQuery_ReturnsTrue()
        {
            // None & None == None => true
            Assert.True(PluginCapability.None.HasCapability(PluginCapability.None));
        }

        [Fact]
        public void HasCapability_AgainstNone_ReturnsFalse()
        {
            Assert.False(PluginCapability.None.HasCapability(PluginCapability.ProvidesIndexer));
        }

        // ── TryParse ──────────────────────────────────────────────────

        [Fact]
        public void TryParse_NullValue_ReturnsFalse()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:140-143
            var result = PluginCapabilityExtensions.TryParse(null!, out var capability);

            Assert.False(result);
            Assert.Equal(PluginCapability.None, capability);
        }

        [Fact]
        public void TryParse_EmptyValue_ReturnsFalse()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:140-143
            var result = PluginCapabilityExtensions.TryParse(string.Empty, out var capability);

            Assert.False(result);
            Assert.Equal(PluginCapability.None, capability);
        }

        [Fact]
        public void TryParse_WhitespaceValue_ReturnsFalse()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:140-143
            var result = PluginCapabilityExtensions.TryParse("   ", out var capability);

            Assert.False(result);
            Assert.Equal(PluginCapability.None, capability);
        }

        [Fact]
        public void TryParse_ValidValue_ReturnsTrueAndCapability()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:145
            var result = PluginCapabilityExtensions.TryParse("search", out var capability);

            Assert.True(result);
            Assert.Equal(PluginCapability.ProvidesIndexer, capability);
        }

        [Fact]
        public void TryParse_InvalidValue_ReturnsFalse()
        {
            var result = PluginCapabilityExtensions.TryParse("not-a-capability", out var capability);

            Assert.False(result);
            Assert.Equal(PluginCapability.None, capability);
        }

        [Fact]
        public void TryParse_CaseInsensitive_ReturnsTrue()
        {
            // NameToCapability uses StringComparer.OrdinalIgnoreCase (line 31)
            var result = PluginCapabilityExtensions.TryParse("DOWNLOAD", out var capability);

            Assert.True(result);
            Assert.Equal(PluginCapability.ProvidesDownloadClient, capability);
        }

        [Fact]
        public void TryParse_WhitespacePaddedValue_TrimsAndParses()
        {
            // src/Abstractions/Capabilities/PluginCapabilityExtensions.cs:145 - value.Trim()
            var result = PluginCapabilityExtensions.TryParse("  quality-selection  ", out var capability);

            Assert.True(result);
            Assert.Equal(PluginCapability.SupportsQualitySelection, capability);
        }
    }
}
