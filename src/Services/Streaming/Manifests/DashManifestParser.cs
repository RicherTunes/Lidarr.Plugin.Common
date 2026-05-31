using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Lidarr.Plugin.Common.Services.Streaming.Manifests
{
    /// <summary>
    /// Spec-correct MPEG-DASH (MPD) manifest parser, shared across plugins whose service delivers
    /// DASH (Amazon Music's <c>getDashManifestsV2</c>, Tidal's <c>application/dash+xml</c>).
    ///
    /// <para>
    /// <b>Parse-only (see <see cref="IStreamManifestParser"/>):</b> turns the MPD index into an ordered
    /// segment list and a variant list, and surfaces any <c>ContentProtection</c> PSSH/KID as opaque
    /// <b>data</b>. It contains no decrypt, key-derivation, or CDM logic.
    /// </para>
    ///
    /// <para>
    /// <b>SegmentTimeline correctness:</b> per the DASH spec (ISO/IEC 23009-1, <c>SegmentTimeline.S@r</c>),
    /// <c>r</c> is the count of <b>additional</b> repeats of a segment of duration <c>d</c>, so an entry
    /// <c>&lt;S t d r=N/&gt;</c> yields <c>N + 1</c> segments. <c>SegmentTemplate@startNumber</c> (default
    /// <c>1</c>) is the <c>$Number$</c> of the first media segment and increments per segment thereafter.
    /// </para>
    /// </summary>
    public sealed class DashManifestParser : IStreamManifestParser
    {
        // Widevine DRM system id used by ContentProtection@schemeIdUri (case-insensitive).
        private const string WidevineSystemId = "edef8ba9-79d6-4ace-a3c8-27dcd51d21ed";

        // Matches $Number%0Nd$ (width-padded $Number$), e.g. $Number%06d$. Group "w" = the N digits of width.
        private static readonly Regex PaddedNumberToken =
            new(@"\$Number%0(?<w>\d+)d\$", RegexOptions.Compiled);

        /// <inheritdoc />
        public bool CanParse(string mimeTypeOrContent)
        {
            if (string.IsNullOrWhiteSpace(mimeTypeOrContent))
            {
                return false;
            }

            string s = mimeTypeOrContent;
            if (s.IndexOf("dash+xml", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // Sniff content: a DASH document's root element is <MPD ...> (optionally with an XML declaration first).
            string trimmed = s.TrimStart();
            if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                int gt = trimmed.IndexOf('>');
                if (gt >= 0 && gt + 1 < trimmed.Length)
                {
                    trimmed = trimmed.Substring(gt + 1).TrimStart();
                }
            }

            return trimmed.StartsWith("<MPD", StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public StreamManifest Parse(string manifestContent, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestContent))
            {
                throw new ArgumentException("Manifest content is empty.", nameof(manifestContent));
            }

            XDocument doc = XDocument.Parse(manifestContent);
            XElement mpd = doc.Root ?? throw new FormatException("DASH manifest has no root element.");
            XNamespace ns = mpd.GetDefaultNamespace();

            // Resolve the effective base: fetch URL, then MPD-level <BaseURL>, then Period-level <BaseURL>.
            string effectiveBase = baseUrl ?? string.Empty;
            effectiveBase = ApplyBaseUrlChild(mpd, ns, effectiveBase);

            XElement? period = mpd.Elements(ns + "Period").FirstOrDefault();
            if (period == null)
            {
                return new StreamManifest(Array.Empty<StreamVariant>(), Array.Empty<StreamSegment>(), string.Empty, ".m4a", false, null, null);
            }

            effectiveBase = ApplyBaseUrlChild(period, ns, effectiveBase);

            // Audio-first: pick the AdaptationSet whose contentType/mimeType is audio when annotated,
            // else the first AdaptationSet (Amazon/Tidal audio manifests typically carry a single set).
            List<XElement> adaptationSets = period.Elements(ns + "AdaptationSet").ToList();
            XElement? adaptationSet =
                adaptationSets.FirstOrDefault(a => IsAudio(a)) ?? adaptationSets.FirstOrDefault();

            if (adaptationSet == null)
            {
                return new StreamManifest(Array.Empty<StreamVariant>(), Array.Empty<StreamSegment>(), string.Empty, ".m4a", false, null, null);
            }

            effectiveBase = ApplyBaseUrlChild(adaptationSet, ns, effectiveBase);

            List<XElement> representations = adaptationSet.Elements(ns + "Representation").ToList();

            // Build the variant list (one per Representation).
            var variants = new List<StreamVariant>(representations.Count);
            foreach (XElement rep in representations)
            {
                int bandwidth = ParseInt(rep.Attribute("bandwidth")?.Value, 0);
                string repCodec = rep.Attribute("codecs")?.Value ?? adaptationSet.Attribute("codecs")?.Value ?? string.Empty;
                string repId = rep.Attribute("id")?.Value ?? string.Empty;
                string variantUrl = VariantUrl(rep, adaptationSet, ns, effectiveBase, repId, bandwidth);
                variants.Add(new StreamVariant(bandwidth, variantUrl, string.IsNullOrEmpty(repCodec) ? null : repCodec));
            }

            // Generate segments from the highest-bandwidth representation (the canonical rendition to fetch).
            XElement? chosen = representations
                .OrderByDescending(r => ParseInt(r.Attribute("bandwidth")?.Value, 0))
                .FirstOrDefault();

            string codec = chosen?.Attribute("codecs")?.Value
                           ?? adaptationSet.Attribute("codecs")?.Value
                           ?? string.Empty;

            List<StreamSegment> segments = chosen != null
                ? BuildSegments(chosen, adaptationSet, ns, effectiveBase)
                : new List<StreamSegment>();

            string fileExtension = DetermineFileExtension(codec, segments);

            (string? pssh, string? keyId) = ExtractContentProtection(adaptationSet, chosen, ns);
            bool isEncrypted = !string.IsNullOrWhiteSpace(pssh) || !string.IsNullOrWhiteSpace(keyId);

            return new StreamManifest(variants, segments, codec, fileExtension, isEncrypted, keyId, pssh);
        }

        private static bool IsAudio(XElement adaptationSet)
        {
            string contentType = adaptationSet.Attribute("contentType")?.Value ?? string.Empty;
            string mimeType = adaptationSet.Attribute("mimeType")?.Value ?? string.Empty;
            return contentType.Equals("audio", StringComparison.OrdinalIgnoreCase)
                   || mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Applies a single child <c>&lt;BaseURL&gt;</c> of <paramref name="element"/> on top of
        /// <paramref name="current"/>, resolving it as relative when it is not already absolute.
        /// </summary>
        private static string ApplyBaseUrlChild(XElement element, XNamespace ns, string current)
        {
            string? child = element.Elements(ns + "BaseURL").FirstOrDefault()?.Value?.Trim();
            if (string.IsNullOrEmpty(child))
            {
                return current;
            }

            return ResolveUrl(current, child);
        }

        private static string VariantUrl(
            XElement rep, XElement adaptationSet, XNamespace ns, string baseUrl, string repId, int bandwidth)
        {
            // Prefer the representation's own <BaseURL> when present; otherwise expose the (resolved)
            // media template as the variant's identifying URL so callers have something to key on.
            string? repBase = rep.Elements(ns + "BaseURL").FirstOrDefault()?.Value?.Trim();
            if (!string.IsNullOrEmpty(repBase))
            {
                return ResolveUrl(baseUrl, repBase);
            }

            XElement? template = rep.Elements(ns + "SegmentTemplate").FirstOrDefault()
                                 ?? adaptationSet.Elements(ns + "SegmentTemplate").FirstOrDefault();
            string? media = template?.Attribute("media")?.Value;
            if (!string.IsNullOrEmpty(media))
            {
                string substituted = SubstituteTemplate(media!, repId, bandwidth, null);
                return ResolveUrl(baseUrl, substituted);
            }

            return baseUrl;
        }

        /// <summary>
        /// Builds the ordered segment list for a representation. Honors (in priority order):
        /// the init segment from <c>SegmentTemplate@initialization</c> (index 0), then either a
        /// <c>SegmentTimeline</c> (expanding each <c>&lt;S t d r/&gt;</c> to <c>r + 1</c> segments) or a
        /// duration-based <c>SegmentTemplate</c> (<c>@duration</c> + media-presentation/period duration),
        /// using <c>@startNumber</c> (default <c>1</c>) as the first <c>$Number$</c>.
        /// </summary>
        private static List<StreamSegment> BuildSegments(
            XElement rep, XElement adaptationSet, XNamespace ns, string baseUrl)
        {
            var segments = new List<StreamSegment>();
            string repId = rep.Attribute("id")?.Value ?? string.Empty;
            int bandwidth = ParseInt(rep.Attribute("bandwidth")?.Value, 0);

            XElement? template = rep.Elements(ns + "SegmentTemplate").FirstOrDefault()
                                 ?? adaptationSet.Elements(ns + "SegmentTemplate").FirstOrDefault();

            // No SegmentTemplate: fall back to a single media segment from <BaseURL>, if any.
            if (template == null)
            {
                string? single = rep.Elements(ns + "BaseURL").FirstOrDefault()?.Value?.Trim();
                if (!string.IsNullOrEmpty(single))
                {
                    segments.Add(new StreamSegment(0, ResolveUrl(baseUrl, single!), null));
                }

                return segments;
            }

            int timescale = ParseInt(template.Attribute("timescale")?.Value, 1);
            if (timescale <= 0)
            {
                timescale = 1;
            }

            // startNumber: the $Number$ of the FIRST media segment (default 1 per spec).
            long startNumber = ParseLong(template.Attribute("startNumber")?.Value, 1);

            int index = 0;

            // Init segment is index 0 when present. It is NOT a numbered media segment, so $Number$ is not substituted here.
            string? initialization = template.Attribute("initialization")?.Value;
            if (!string.IsNullOrEmpty(initialization))
            {
                string initUrl = ResolveUrl(baseUrl, SubstituteTemplate(initialization!, repId, bandwidth, null));
                segments.Add(new StreamSegment(index++, initUrl, null));
            }

            string media = template.Attribute("media")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(media))
            {
                return segments;
            }

            XElement? timeline = template.Elements(ns + "SegmentTimeline").FirstOrDefault();
            if (timeline != null)
            {
                long number = startNumber;
                foreach (XElement s in timeline.Elements(ns + "S"))
                {
                    long d = ParseLong(s.Attribute("d")?.Value, 0);
                    // r = ADDITIONAL repeats -> r + 1 segments total for this <S>. (A negative r meaning
                    // "repeat to the end" is not used by Amazon/Tidal audio manifests; treat <0 as 0.)
                    int r = ParseInt(s.Attribute("r")?.Value, 0);
                    if (r < 0)
                    {
                        r = 0;
                    }

                    double? durationSeconds = d > 0 ? d / (double)timescale : (double?)null;

                    for (int i = 0; i <= r; i++)
                    {
                        string url = ResolveUrl(baseUrl, SubstituteTemplate(media, repId, bandwidth, number));
                        segments.Add(new StreamSegment(index++, url, durationSeconds));
                        number++;
                    }
                }

                return segments;
            }

            // Duration-based SegmentTemplate (no timeline): segment count = ceil(totalDuration / segmentDuration).
            long segDuration = ParseLong(template.Attribute("duration")?.Value, 0);
            double totalSeconds = ParseDuration(
                rep.Document?.Root?.Attribute("mediaPresentationDuration")?.Value
                ?? (rep.Ancestors(ns + "Period").FirstOrDefault()?.Attribute("duration")?.Value));

            if (segDuration > 0 && totalSeconds > 0)
            {
                double segSeconds = segDuration / (double)timescale;
                int count = (int)Math.Ceiling(totalSeconds / segSeconds);
                long number = startNumber;
                for (int i = 0; i < count; i++)
                {
                    string url = ResolveUrl(baseUrl, SubstituteTemplate(media, repId, bandwidth, number));
                    segments.Add(new StreamSegment(index++, url, segSeconds));
                    number++;
                }
            }

            return segments;
        }

        /// <summary>
        /// Substitutes the DASH identifier tokens in a SegmentTemplate string: <c>$RepresentationID$</c>,
        /// <c>$Bandwidth$</c>, <c>$Number$</c>, and width-padded <c>$Number%0Nd$</c>. <c>$$</c> is the
        /// literal-dollar escape. A null <paramref name="number"/> leaves <c>$Number$</c> tokens untouched
        /// (used for the init segment / variant identity).
        /// </summary>
        private static string SubstituteTemplate(string template, string repId, int bandwidth, long? number)
        {
            string result = template
                .Replace("$RepresentationID$", repId)
                .Replace("$Bandwidth$", bandwidth.ToString(CultureInfo.InvariantCulture));

            if (number.HasValue)
            {
                result = PaddedNumberToken.Replace(
                    result,
                    m =>
                    {
                        int width = int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);
                        return number.Value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
                    });
                result = result.Replace("$Number$", number.Value.ToString(CultureInfo.InvariantCulture));
            }

            // Unescape $$ last so a literal dollar in the source can't be misread as a token delimiter.
            result = result.Replace("$$", "$");
            return result;
        }

        /// <summary>
        /// Extracts Widevine PSSH (<c>cenc:pssh</c>) and KID (<c>@cenc:default_KID</c>) from
        /// <c>ContentProtection</c> elements as <b>opaque data</b>. Prefers the Widevine-scheme element for
        /// PSSH; falls back to any <c>cenc:pssh</c>. KID is read from the <c>mp4protection</c>/<c>cenc</c>
        /// scheme's <c>default_KID</c> when present. No key handling is performed.
        /// </summary>
        private static (string? Pssh, string? KeyId) ExtractContentProtection(
            XElement adaptationSet, XElement? representation, XNamespace ns)
        {
            XNamespace cenc = "urn:mpeg:cenc:2013";

            IEnumerable<XElement> protections = adaptationSet.Elements(ns + "ContentProtection");
            if (representation != null)
            {
                protections = protections.Concat(representation.Elements(ns + "ContentProtection"));
            }

            List<XElement> all = protections.ToList();

            string? keyId = null;
            foreach (XElement cp in all)
            {
                string? kid = cp.Attribute(cenc + "default_KID")?.Value
                              ?? cp.Attribute("default_KID")?.Value;
                if (!string.IsNullOrWhiteSpace(kid))
                {
                    keyId = kid.Trim();
                    break;
                }
            }

            // Prefer the Widevine-scheme ContentProtection's cenc:pssh; otherwise any cenc:pssh present.
            string? pssh = null;
            XElement? widevine = all.FirstOrDefault(cp =>
                (cp.Attribute("schemeIdUri")?.Value ?? string.Empty)
                .IndexOf(WidevineSystemId, StringComparison.OrdinalIgnoreCase) >= 0);

            XElement? psshElement =
                widevine?.Elements(cenc + "pssh").FirstOrDefault()
                ?? all.SelectMany(cp => cp.Elements(cenc + "pssh")).FirstOrDefault();

            string? psshValue = psshElement?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(psshValue))
            {
                pssh = psshValue;
            }

            return (pssh, keyId);
        }

        private static string DetermineFileExtension(string codec, IReadOnlyList<StreamSegment> segments)
        {
            // DASH audio is delivered in an MP4/M4A container regardless of the codec inside (incl. FLAC-in-MP4).
            // Prefer a concrete extension sniffed from a segment URL when present.
            foreach (StreamSegment seg in segments)
            {
                string u = seg.Url;
                if (u.IndexOf(".mp4", StringComparison.OrdinalIgnoreCase) >= 0
                    || u.IndexOf(".m4s", StringComparison.OrdinalIgnoreCase) >= 0
                    || u.IndexOf(".m4a", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ".m4a";
                }
            }

            if (!string.IsNullOrEmpty(codec) && codec.IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ".m4a";
            }

            return ".m4a";
        }

        /// <summary>
        /// Resolves <paramref name="reference"/> against <paramref name="baseUrl"/>. Absolute references
        /// are returned as-is; when no usable base is available the reference is returned unchanged.
        /// </summary>
        private static string ResolveUrl(string baseUrl, string reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return baseUrl ?? string.Empty;
            }

            if (Uri.TryCreate(reference, UriKind.Absolute, out Uri? abs))
            {
                return abs.ToString();
            }

            if (!string.IsNullOrEmpty(baseUrl)
                && Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri)
                && Uri.TryCreate(baseUri, reference, out Uri? combined))
            {
                return combined.ToString();
            }

            return reference;
        }

        private static int ParseInt(string? value, int fallback) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static long ParseLong(string? value, long fallback) =>
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : fallback;

        /// <summary>Parses an ISO-8601 duration (e.g. <c>PT3M30.5S</c>) to seconds; returns 0 when absent/invalid.</summary>
        private static double ParseDuration(string? iso)
        {
            if (string.IsNullOrWhiteSpace(iso))
            {
                return 0;
            }

            try
            {
                return XmlConvertDuration(iso!);
            }
            catch (FormatException)
            {
                return 0;
            }
            catch (OverflowException)
            {
                return 0;
            }
        }

        private static double XmlConvertDuration(string iso) =>
            System.Xml.XmlConvert.ToTimeSpan(iso).TotalSeconds;
    }
}
