using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lidarr.Plugin.Common.Services.Streaming.Manifests
{
    /// <summary>
    /// Spec-correct HLS (RFC 8216 / <c>.m3u8</c>) playlist parser, shared across plugins whose service
    /// delivers HLS (Apple Music's per-track <c>.m3u8</c>). Slots in alongside
    /// <see cref="DashManifestParser"/> behind the <see cref="IStreamManifestParser"/> seam so callers
    /// parse HLS through one shared code path instead of an inline per-plugin walk.
    ///
    /// <para>
    /// <b>Parse-only (see <see cref="IStreamManifestParser"/>):</b> this turns the playlist index into a
    /// variant list (for a master playlist) or an ordered segment list (for a media playlist), and
    /// surfaces any <c>#EXT-X-KEY</c> as <b>opaque data</b> (<see cref="StreamManifest.IsEncrypted"/> /
    /// <see cref="StreamManifest.KeyId"/>). It contains no decrypt, key-derivation, or key-fetch logic;
    /// the <c>#EXT-X-KEY URI</c> is deliberately NOT followed.
    /// </para>
    ///
    /// <para>
    /// <b>Master vs media:</b> a playlist that carries any <c>#EXT-X-STREAM-INF</c> line is a master
    /// playlist; <see cref="Parse"/> returns its renditions in <see cref="StreamManifest.Variants"/> with
    /// an empty <see cref="StreamManifest.Segments"/>. The caller selects a variant (e.g. via
    /// <see cref="QualitySelector"/>) and re-parses that variant's playlist to obtain the segment list —
    /// mirroring how <see cref="DashManifestParser"/> exposes <c>Representation</c>s and how Apple's
    /// downloader follows the chosen variant.
    /// </para>
    /// </summary>
    public sealed class HlsManifestParser : IStreamManifestParser
    {
        // CODECS="a,b" attribute on #EXT-X-STREAM-INF (quoted, comma-separated list).
        private static readonly Regex CodecsAttr =
            new("CODECS=\"(?<v>[^\"]*)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // BANDWIDTH=<int> attribute on #EXT-X-STREAM-INF (unquoted decimal).
        private static readonly Regex BandwidthAttr =
            new(@"BANDWIDTH=(?<v>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <inheritdoc />
        public bool CanParse(string mimeTypeOrContent)
        {
            if (string.IsNullOrWhiteSpace(mimeTypeOrContent))
            {
                return false;
            }

            string s = mimeTypeOrContent;

            // MIME types Apple/HLS services advertise for playlists.
            if (s.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // A URL/filename ending in the playlist extension (allow a query string after it).
            if (s.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // Sniff content: an HLS playlist's first line is the #EXTM3U tag.
            return s.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public StreamManifest Parse(string manifestContent, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestContent))
            {
                throw new ArgumentException("Manifest content is empty.", nameof(manifestContent));
            }

            // Split on both \n and \r so CRLF playlists don't leave a trailing \r on every line.
            string[] lines = manifestContent
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

            bool isMaster = lines.Any(l =>
                l.TrimStart().StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase));

            return isMaster
                ? ParseMaster(lines, baseUrl)
                : ParseMedia(lines, baseUrl);
        }

        /// <summary>
        /// Parses a master playlist into the variant list. Each <c>#EXT-X-STREAM-INF</c> tag (carrying
        /// <c>BANDWIDTH</c>/<c>CODECS</c>) is paired with the immediately following non-comment line (the
        /// variant playlist URI, resolved against <paramref name="baseUrl"/>). Segments are empty: the
        /// caller selects a variant and re-parses it. The manifest-level <see cref="StreamManifest.Codec"/>
        /// reflects the highest-bandwidth variant.
        /// </summary>
        private static StreamManifest ParseMaster(IReadOnlyList<string> lines, string baseUrl)
        {
            var variants = new List<StreamVariant>();
            int pendingBandwidth = -1;
            string? pendingCodec = null;
            bool pending = false;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    pending = true;
                    pendingBandwidth = ParseBandwidth(line);
                    pendingCodec = ParseCodec(line);
                    continue;
                }

                // Other tags between the stream-inf and its URI (rare) are ignored, not the URI.
                if (line.StartsWith('#'))
                {
                    continue;
                }

                if (pending)
                {
                    string url = ResolveUrl(baseUrl, line);
                    variants.Add(new StreamVariant(pendingBandwidth < 0 ? 0 : pendingBandwidth, url, pendingCodec));
                    pending = false;
                    pendingBandwidth = -1;
                    pendingCodec = null;
                }
            }

            // Manifest-level codec/extension reflect the highest-bandwidth rendition (the canonical pick).
            StreamVariant? top = variants
                .OrderByDescending(v => v.BandwidthBps)
                .FirstOrDefault();
            string codec = top?.Codec ?? string.Empty;
            string fileExtension = DetermineFileExtension(codec, Array.Empty<StreamSegment>());

            // A master playlist itself declares no segments and no content protection.
            return new StreamManifest(variants, Array.Empty<StreamSegment>(), codec, fileExtension, false, null, null);
        }

        /// <summary>
        /// Parses a media (variant) playlist into the ordered segment list. <c>#EXTINF:&lt;dur&gt;,&lt;title&gt;</c>
        /// supplies each segment's duration; the following non-comment line is the segment URI (resolved
        /// against <paramref name="baseUrl"/>). An <c>#EXT-X-KEY</c> with a <c>METHOD</c> other than
        /// <c>NONE</c> flags <see cref="StreamManifest.IsEncrypted"/> and surfaces its <c>KEYID</c> (when
        /// present) as opaque <see cref="StreamManifest.KeyId"/> — no key is fetched or applied.
        /// </summary>
        private static StreamManifest ParseMedia(IReadOnlyList<string> lines, string baseUrl)
        {
            var segments = new List<StreamSegment>();
            double? pendingDuration = null;
            int index = 0;

            bool isEncrypted = false;
            string? keyId = null;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith('#'))
                {
                    if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingDuration = ParseExtInfDuration(line);
                    }
                    else if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyKeyTag(line, ref isEncrypted, ref keyId);
                    }

                    continue;
                }

                // A non-comment line is a media segment URI.
                string url = ResolveUrl(baseUrl, line);
                segments.Add(new StreamSegment(index++, url, pendingDuration));
                pendingDuration = null;
            }

            string codec = string.Empty;
            string fileExtension = DetermineFileExtension(codec, segments);

            // A media playlist exposes segments, not selectable variants. HLS carries no PSSH (that is a
            // DASH/CENC construct), so KeyId is the only content-protection datum.
            return new StreamManifest(Array.Empty<StreamVariant>(), segments, codec, fileExtension, isEncrypted, keyId, null);
        }

        /// <summary>
        /// Reads an <c>#EXT-X-KEY</c> tag. Sets <paramref name="isEncrypted"/> when <c>METHOD</c> is present
        /// and not <c>NONE</c>, and captures <c>KEYID</c> (hex KID, opaque) when present. The key <c>URI</c>
        /// is intentionally ignored — this parser never fetches or applies keys.
        /// </summary>
        private static void ApplyKeyTag(string line, ref bool isEncrypted, ref string? keyId)
        {
            string? method = ParseAttribute(line, "METHOD");

            // METHOD=NONE explicitly means "no encryption" and clears any prior key (per RFC 8216).
            if (string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase))
            {
                isEncrypted = false;
                keyId = null;
                return;
            }

            if (!string.IsNullOrWhiteSpace(method))
            {
                isEncrypted = true;
            }

            string? kid = ParseAttribute(line, "KEYID");
            if (!string.IsNullOrWhiteSpace(kid))
            {
                keyId = kid;
            }
        }

        private static int ParseBandwidth(string streamInfLine)
        {
            Match m = BandwidthAttr.Match(streamInfLine);
            return m.Success && int.TryParse(m.Groups["v"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bw)
                ? bw
                : -1;
        }

        private static string? ParseCodec(string streamInfLine)
        {
            Match m = CodecsAttr.Match(streamInfLine);
            if (!m.Success)
            {
                return null;
            }

            // CODECS may list several (audio,video); the first entry is the primary codec.
            string list = m.Groups["v"].Value.Trim();
            if (list.Length == 0)
            {
                return null;
            }

            string first = list.Split(',')[0].Trim();
            return first.Length == 0 ? null : first;
        }

        /// <summary>Parses the duration from <c>#EXTINF:&lt;seconds&gt;,&lt;optional title&gt;</c>.</summary>
        private static double? ParseExtInfDuration(string extInfLine)
        {
            int colon = extInfLine.IndexOf(':');
            if (colon < 0 || colon + 1 >= extInfLine.Length)
            {
                return null;
            }

            string value = extInfLine.Substring(colon + 1);
            int comma = value.IndexOf(',');
            string number = comma >= 0 ? value.Substring(0, comma) : value;
            number = number.Trim();

            return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                ? seconds
                : (double?)null;
        }

        /// <summary>
        /// Extracts a comma-separated HLS attribute (e.g. <c>METHOD</c>, <c>KEYID</c>) from a tag line.
        /// Handles both quoted (<c>URI="..."</c>) and unquoted (<c>METHOD=AES-128</c>) values, and ignores
        /// matches inside an earlier quoted value.
        /// </summary>
        private static string? ParseAttribute(string line, string name)
        {
            // Strip the leading "#TAG:" so the attribute scan starts at the attribute list.
            int colon = line.IndexOf(':');
            string attrs = colon >= 0 && colon + 1 < line.Length ? line.Substring(colon + 1) : line;

            int i = 0;
            int n = attrs.Length;
            while (i < n)
            {
                // Read an attribute name up to '='.
                int eq = attrs.IndexOf('=', i);
                if (eq < 0)
                {
                    break;
                }

                string key = attrs.Substring(i, eq - i).Trim();
                int valStart = eq + 1;
                string value;
                int next;

                if (valStart < n && attrs[valStart] == '"')
                {
                    // Quoted value: read to the closing quote.
                    int close = attrs.IndexOf('"', valStart + 1);
                    if (close < 0)
                    {
                        value = attrs.Substring(valStart + 1);
                        next = n;
                    }
                    else
                    {
                        value = attrs.Substring(valStart + 1, close - valStart - 1);
                        // Skip past the closing quote and the following comma, if any.
                        next = close + 1;
                        int afterComma = attrs.IndexOf(',', next);
                        next = afterComma < 0 ? n : afterComma + 1;
                    }
                }
                else
                {
                    // Unquoted value: read to the next comma.
                    int comma = attrs.IndexOf(',', valStart);
                    if (comma < 0)
                    {
                        value = attrs.Substring(valStart);
                        next = n;
                    }
                    else
                    {
                        value = attrs.Substring(valStart, comma - valStart);
                        next = comma + 1;
                    }
                }

                if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return value.Trim();
                }

                i = next;
            }

            return null;
        }

        private static string DetermineFileExtension(string codec, IReadOnlyList<StreamSegment> segments)
        {
            // Prefer a concrete extension sniffed from a segment URL when present.
            foreach (StreamSegment seg in segments)
            {
                string u = seg.Url;
                if (u.IndexOf(".m4a", StringComparison.OrdinalIgnoreCase) >= 0
                    || u.IndexOf(".m4s", StringComparison.OrdinalIgnoreCase) >= 0
                    || u.IndexOf(".mp4", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ".m4a";
                }

                if (u.IndexOf(".aac", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ".aac";
                }

                if (u.IndexOf(".ts", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ".ts";
                }
            }

            // AAC-LC (mp4a.40.2) and ALAC (alac) are delivered fragmented-MP4 by Apple Music.
            if (!string.IsNullOrEmpty(codec)
                && (codec.IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) >= 0
                    || codec.IndexOf("alac", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return ".m4a";
            }

            return ".m4a";
        }

        /// <summary>
        /// Resolves <paramref name="reference"/> against <paramref name="baseUrl"/>. Absolute references
        /// are returned as-is; scheme-relative (<c>//host/path</c>) references take the base's scheme; when
        /// no usable base is available the reference is returned unchanged.
        /// </summary>
        private static string ResolveUrl(string baseUrl, string reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return baseUrl ?? string.Empty;
            }

            if (Uri.TryCreate(reference, UriKind.Absolute, out Uri? abs)
                && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            {
                return abs.ToString();
            }

            if (string.IsNullOrEmpty(baseUrl)
                || !Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
            {
                return reference;
            }

            // Scheme-relative (//host/path): .NET won't resolve these via the (base, relative) overload,
            // so prepend the base scheme explicitly.
            if (reference.StartsWith("//", StringComparison.Ordinal)
                && Uri.TryCreate(baseUri.Scheme + ":" + reference, UriKind.Absolute, out Uri? schemeRelative))
            {
                return schemeRelative.ToString();
            }

            return Uri.TryCreate(baseUri, reference, out Uri? combined)
                ? combined.ToString()
                : reference;
        }
    }
}
