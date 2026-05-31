using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Drm
{
    /// <summary>
    /// Minimal, <b>service-neutral</b> DRM/playback metadata for a single track, passed across
    /// the <see cref="IExternalDownloadHandler"/> seam to an out-of-tree downloader/decryptor.
    ///
    /// <para>
    /// This is the shared, neutralized promotion of each DRM plugin's local track model. It carries
    /// only the data a generic fetch/decrypt/mux step needs and contains <b>no decrypt logic, no key
    /// material derivation, and no service-specific behavior</b> — see <see cref="IExternalDownloadHandler"/>
    /// for the DRM-out-of-Common stance. Service-specific identifiers (e.g. Amazon's ASIN, a license
    /// endpoint, an Apple Adam ID) belong in <see cref="ServiceHints"/> so this model stays neutral
    /// for both HLS and DASH sources and across services.
    /// </para>
    /// </summary>
    public sealed class DrmTrack : ICloneable
    {
        /// <summary>Service-specific track identifier (opaque to Common).</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Track title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Track artists.</summary>
        public List<string> Artists { get; set; } = new();

        /// <summary>
        /// URL of the streaming manifest. Neutral across protocols: an HLS <c>.m3u8</c> playlist
        /// (Apple Music) or a DASH <c>.mpd</c> manifest (Amazon Music). Renamed from the
        /// Apple-specific <c>M3U8Url</c> during promotion to Common.
        /// </summary>
        public string ManifestUrl { get; set; } = string.Empty;

        /// <summary>Base64 PSSH box carrying DRM init data. Empty when the track is not DRM-protected.</summary>
        public string Pssh { get; set; } = string.Empty;

        /// <summary>DRM key identifier (KID), when known.</summary>
        public string KeyId { get; set; } = string.Empty;

        /// <summary>Target/selected bitrate in kbps, when known.</summary>
        public int BitrateKbps { get; set; }

        /// <summary>Optional originating URL hint for handlers (album/playlist/song page).</summary>
        public string? SourceUrl { get; set; }

        /// <summary>
        /// Free-form, service-specific hints for the out-of-tree handler (e.g. Amazon ASIN,
        /// license-acquisition endpoint, marketplace/region). Keeps <see cref="DrmTrack"/> neutral
        /// while letting each plugin pass what its decryptor needs. Never interpreted by Common.
        /// </summary>
        public Dictionary<string, string> ServiceHints { get; set; } = new();

        /// <summary>
        /// True when DRM init data (<see cref="Pssh"/>) is present, indicating the track needs a
        /// license challenge / decrypt step. This is the only "logic" in the model — a presence
        /// check, not any form of key handling.
        /// </summary>
        public bool IsDrmProtected => !string.IsNullOrWhiteSpace(Pssh);

        /// <summary>
        /// Returns a deep copy (the <see cref="Artists"/> list and <see cref="ServiceHints"/>
        /// dictionary are cloned, not shared) so a handler can mutate its copy safely.
        /// </summary>
        public object Clone()
        {
            return new DrmTrack
            {
                Id = Id,
                Title = Title,
                Artists = new List<string>(Artists),
                ManifestUrl = ManifestUrl,
                Pssh = Pssh,
                KeyId = KeyId,
                BitrateKbps = BitrateKbps,
                SourceUrl = SourceUrl,
                ServiceHints = new Dictionary<string, string>(ServiceHints)
            };
        }
    }
}
