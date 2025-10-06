using System;

namespace Lidarr.Plugin.Abstractions.Capabilities
{
    /// <summary>
    /// Flags describing the behavioural capabilities a plugin exposes to the host.
    /// </summary>
    [Flags]
    public enum PluginCapability
    {
        None = 0,

        /// <summary>A searchable indexer is provided.</summary>
        ProvidesIndexer = 1 << 0,

        /// <summary>A download client is provided.</summary>
        ProvidesDownloadClient = 1 << 1,

        /// <summary>Plugin implements caching for upstream requests.</summary>
        SupportsCaching = 1 << 2,

        /// <summary>Plugin performs authentication (OAuth, API keys, etc.).</summary>
        SupportsAuthentication = 1 << 3,

        /// <summary>Plugin allows the caller to select between multiple qualities or formats.</summary>
        SupportsQualitySelection = 1 << 4,

        /// <summary>Plugin can complete OAuth 2.0 authorization-code flows.</summary>
        SupportsOAuth = 1 << 5,

        /// <summary>Plugin supports OAuth 2.0 device code or similar out-of-band flows.</summary>
        SupportsDeviceCode = 1 << 6,

        /// <summary>Plugin can deliver hi-res / lossless audio variants.</summary>
        SupportsHiResAudio = 1 << 7,

        /// <summary>Plugin can provide short playback previews for tracks.</summary>
        SupportsTrackPreviews = 1 << 8,

        /// <summary>Plugin can orchestrate batched / queued downloads efficiently.</summary>
        SupportsBatchDownloads = 1 << 9,

        /// <summary>Plugin is aware of regional catalog variants and can target specific markets.</summary>
        SupportsRegionalCatalogs = 1 << 10,

        /// <summary>Plugin supports lookup by ISRC.</summary>
        SupportsIsrcSearch = 1 << 11,

        /// <summary>Plugin supports lookup by UPC / EAN.</summary>
        SupportsUpcSearch = 1 << 12,

        /// <summary>Plugin can surface curated or user playlists.</summary>
        SupportsPlaylists = 1 << 13
    }
}
