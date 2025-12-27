namespace Lidarr.Plugin.Abstractions.Models
{
    /// <summary>
    /// Standard metadata keys for consistent cross-plugin streaming data.
    /// Use these constants instead of magic strings for Metadata dictionary keys.
    /// </summary>
    public static class StreamingMetadataKeys
    {
        /// <summary>
        /// Total number of discs/volumes in a multi-disc album.
        /// Value type: int (default: 1 for single-disc albums).
        /// </summary>
        public const string TotalDiscs = "TotalDiscs";

        /// <summary>
        /// Audio quality tier/level from the streaming service.
        /// Value type: string (e.g., "lossless", "hires", "mp3_320").
        /// </summary>
        public const string Quality = "quality";

        /// <summary>
        /// Service-specific identifier for the item.
        /// Value type: string.
        /// </summary>
        public const string ServiceId = "service_id";

        /// <summary>
        /// Original service name that provided this data.
        /// Value type: string (e.g., "tidal", "qobuz").
        /// </summary>
        public const string ServiceName = "service_name";

        /// <summary>
        /// Whether the track/album is available for streaming.
        /// Value type: bool.
        /// </summary>
        public const string IsAvailable = "is_available";

        /// <summary>
        /// Whether the content has explicit lyrics/content.
        /// Value type: bool.
        /// </summary>
        public const string IsExplicit = "is_explicit";

        /// <summary>
        /// Release version or edition information.
        /// Value type: string (e.g., "Deluxe Edition", "Remastered").
        /// </summary>
        public const string Version = "version";

        /// <summary>
        /// Copyright information.
        /// Value type: string.
        /// </summary>
        public const string Copyright = "copyright";
    }
}
