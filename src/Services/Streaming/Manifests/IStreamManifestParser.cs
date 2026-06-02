using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Streaming.Manifests
{
    /// <summary>
    /// Parser seam for streaming-service playback manifests (the small index document a
    /// service returns describing how its already-licensed media is segmented and where each
    /// segment lives). Implementations turn a manifest document into the protocol-neutral
    /// <see cref="StreamManifest"/> shape so plugins parse manifests through one shared code path
    /// instead of each maintaining its own subtly-divergent parser.
    ///
    /// <para>
    /// <b>Parsing-only stance (no circumvention):</b> reading a manifest is reading a playlist —
    /// it enumerates segment URLs the same way an <c>.m3u8</c> or <c>.mpd</c> does. This seam and its
    /// implementations contain <b>NO decrypt logic, NO key derivation, and NO CDM / license-challenge
    /// code</b>. When a manifest declares content protection, the parser surfaces the
    /// <see cref="StreamManifest.Pssh"/> and <see cref="StreamManifest.KeyId"/> values <b>as opaque
    /// data only</b> and flags <see cref="StreamManifest.IsEncrypted"/>; any actual decryption is the
    /// responsibility of an out-of-tree handler (see
    /// <see cref="Lidarr.Plugin.Common.Services.Drm.IExternalDownloadHandler"/>).
    /// </para>
    ///
    /// <para>
    /// The seam is deliberately small so a second protocol (e.g. HLS for Apple Music) can slot in as
    /// another <see cref="IStreamManifestParser"/> without changing callers:
    /// <see cref="DashManifestParser"/> handles MPEG-DASH (Amazon Music's <c>getDashManifestsV2</c>,
    /// Tidal's <c>application/dash+xml</c>); an HLS parser would handle <c>.m3u8</c>.
    /// </para>
    /// </summary>
    public interface IStreamManifestParser
    {
        /// <summary>
        /// Returns <c>true</c> when this parser can handle the supplied manifest, identified either by
        /// MIME type (e.g. <c>application/dash+xml</c>) or by a sniff of the manifest content itself
        /// (e.g. an opening <c>&lt;MPD</c> tag). Callers may pass either; implementations should accept both.
        /// </summary>
        /// <param name="mimeTypeOrContent">A MIME type string, or the (start of the) manifest content.</param>
        bool CanParse(string mimeTypeOrContent);

        /// <summary>
        /// Parses <paramref name="manifestContent"/> into the protocol-neutral <see cref="StreamManifest"/>.
        /// Relative segment / variant URLs are resolved against <paramref name="baseUrl"/> (and any
        /// manifest-internal base, e.g. DASH <c>&lt;BaseURL&gt;</c>).
        /// </summary>
        /// <param name="manifestContent">The raw manifest document (already decoded text, not base64).</param>
        /// <param name="baseUrl">
        /// Absolute URL the manifest was fetched from, used to resolve relative URLs. May be empty when
        /// the manifest only contains absolute URLs.
        /// </param>
        StreamManifest Parse(string manifestContent, string baseUrl);
    }

    /// <summary>
    /// Protocol-neutral result of parsing a streaming manifest. Carries the selectable
    /// <see cref="Variants"/> (quality renditions), the ordered <see cref="Segments"/> to fetch, and
    /// content-protection <b>data</b> (<see cref="Pssh"/> / <see cref="KeyId"/>) when present — never
    /// any key/decrypt logic (see <see cref="IStreamManifestParser"/>).
    /// </summary>
    /// <param name="Variants">Selectable quality renditions, in manifest order. May be empty.</param>
    /// <param name="Segments">Ordered media segments to fetch (an init segment, when present, is index 0).</param>
    /// <param name="Codec">Best-known codec string for the selected/first representation (e.g. <c>flac</c>, <c>mp4a.40.2</c>), or empty.</param>
    /// <param name="FileExtension">Container file extension for the fetched media (e.g. <c>.m4a</c>, <c>.mp4</c>).</param>
    /// <param name="IsEncrypted">True when the manifest declares content protection (i.e. <see cref="Pssh"/> or <see cref="KeyId"/> is present).</param>
    /// <param name="KeyId">DRM key identifier (KID) as declared in the manifest, or <c>null</c>. Opaque data only.</param>
    /// <param name="Pssh">Base64 PSSH init data (Widevine) as declared in the manifest, or <c>null</c>. Opaque data only.</param>
    public sealed record StreamManifest(
        IReadOnlyList<StreamVariant> Variants,
        IReadOnlyList<StreamSegment> Segments,
        string Codec,
        string FileExtension,
        bool IsEncrypted,
        string? KeyId,
        string? Pssh);

    /// <summary>A selectable quality rendition (one DASH <c>Representation</c> / one HLS variant playlist).</summary>
    /// <param name="BandwidthBps">Declared bandwidth in bits per second (the value quality selection ranks on).</param>
    /// <param name="Url">Resolved URL identifying the variant (its media template / playlist), absolute when resolvable.</param>
    /// <param name="Codec">Codec string for this variant (e.g. <c>flac</c>, <c>mp4a.40.2</c>), or <c>null</c> when unspecified.</param>
    public sealed record StreamVariant(int BandwidthBps, string Url, string? Codec);

    /// <summary>A single ordered media segment to fetch.</summary>
    /// <param name="Index">Zero-based position of the segment within <see cref="StreamManifest.Segments"/>.</param>
    /// <param name="Url">Resolved segment URL, absolute when resolvable against the base URL.</param>
    /// <param name="DurationSeconds">Segment duration in seconds when derivable from the manifest timescale, else <c>null</c>.</param>
    public sealed record StreamSegment(int Index, string Url, double? DurationSeconds);
}
