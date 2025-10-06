using System;
using System.Collections.Generic;
using System.Linq;

namespace Lidarr.Plugin.Abstractions.Capabilities
{
    /// <summary>
    /// Helper utilities for converting between manifest capability strings and strongly typed flags.
    /// </summary>
    public static class PluginCapabilityExtensions
    {
        private static readonly IReadOnlyDictionary<PluginCapability, string> ManifestNames = new Dictionary<PluginCapability, string>
        {
            [PluginCapability.ProvidesIndexer] = "search",
            [PluginCapability.ProvidesDownloadClient] = "download",
            [PluginCapability.SupportsCaching] = "caching",
            [PluginCapability.SupportsAuthentication] = "authentication",
            [PluginCapability.SupportsQualitySelection] = "quality-selection",
            [PluginCapability.SupportsOAuth] = "oauth",
            [PluginCapability.SupportsDeviceCode] = "device-code",
            [PluginCapability.SupportsHiResAudio] = "hi-res-audio",
            [PluginCapability.SupportsTrackPreviews] = "track-previews",
            [PluginCapability.SupportsBatchDownloads] = "batch-downloads",
            [PluginCapability.SupportsRegionalCatalogs] = "regional-catalogs",
            [PluginCapability.SupportsIsrcSearch] = "search-isrc",
            [PluginCapability.SupportsUpcSearch] = "search-upc",
            [PluginCapability.SupportsPlaylists] = "playlists"
        };

        private static readonly IReadOnlyDictionary<string, PluginCapability> NameToCapability = ManifestNames
            .ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

        private static readonly IReadOnlyDictionary<PluginCapability, string> DisplayNames = new Dictionary<PluginCapability, string>
        {
            [PluginCapability.ProvidesIndexer] = "Search",
            [PluginCapability.ProvidesDownloadClient] = "Download",
            [PluginCapability.SupportsCaching] = "Caching",
            [PluginCapability.SupportsAuthentication] = "Authentication",
            [PluginCapability.SupportsQualitySelection] = "Quality Selection",
            [PluginCapability.SupportsOAuth] = "OAuth",
            [PluginCapability.SupportsDeviceCode] = "Device Code",
            [PluginCapability.SupportsHiResAudio] = "Hi-Res Audio",
            [PluginCapability.SupportsTrackPreviews] = "Track Previews",
            [PluginCapability.SupportsBatchDownloads] = "Batch Downloads",
            [PluginCapability.SupportsRegionalCatalogs] = "Regional Catalogs",
            [PluginCapability.SupportsIsrcSearch] = "Search by ISRC",
            [PluginCapability.SupportsUpcSearch] = "Search by UPC",
            [PluginCapability.SupportsPlaylists] = "Playlists"
        };

        /// <summary>
        /// Converts a set of capability flags into manifest string values.
        /// </summary>
        public static IReadOnlyList<string> ToManifestValues(this PluginCapability capabilities)
        {
            if (capabilities == PluginCapability.None)
            {
                return Array.Empty<string>();
            }

            var values = new List<string>();
            foreach (var kvp in ManifestNames)
            {
                if (capabilities.HasFlag(kvp.Key))
                {
                    values.Add(kvp.Value);
                }
            }

            return values;
        }

        /// <summary>
        /// Converts manifest string values into capability flags.
        /// </summary>
        public static PluginCapability FromManifestValues(IEnumerable<string> values, out IReadOnlyList<string> unknownValues)
        {
            if (values is null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            var flags = PluginCapability.None;
            var unknown = new List<string>();

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (NameToCapability.TryGetValue(value.Trim(), out var capability))
                {
                    flags |= capability;
                }
                else
                {
                    unknown.Add(value);
                }
            }

            unknownValues = unknown;
            return flags;
        }

        /// <summary>
        /// Converts capability flags into display names suitable for human readable views.
        /// </summary>
        public static IReadOnlyList<string> ToDisplayNames(this PluginCapability capabilities)
        {
            if (capabilities == PluginCapability.None)
            {
                return Array.Empty<string>();
            }

            var display = new List<string>();
            foreach (var kvp in DisplayNames)
            {
                if (capabilities.HasFlag(kvp.Key))
                {
                    display.Add(kvp.Value);
                }
            }

            return display;
        }

        /// <summary>
        /// Checks if the capability set contains the requested flag.
        /// </summary>
        public static bool HasCapability(this PluginCapability capabilities, PluginCapability capability) => (capabilities & capability) == capability;

        /// <summary>
        /// Attempts to map a manifest value to a capability flag.
        /// </summary>
        public static bool TryParse(string value, out PluginCapability capability)
        {
            capability = PluginCapability.None;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return NameToCapability.TryGetValue(value.Trim(), out capability);
        }
    }
}
