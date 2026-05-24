using System;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Parse album IDs from Lidarr <c>ReleaseInfo.Guid</c> / <c>ReleaseInfo.InfoUrl</c> shapes
/// shared by all RicherTunes streaming-service plugins.
///
/// <para>The supported GUID grammar is:</para>
/// <code>
///   [{indexerId}_]{scheme}:album:{id}[:{extra}]
/// </code>
///
/// <para>Where:</para>
/// <list type="bullet">
///   <item><c>{indexerId}_</c> — optional. Lidarr prefixes search-result GUIDs with the
///         indexer's numeric ID followed by an underscore (e.g. <c>2_applemusic:album:111</c>).
///         The parser strips it transparently.</item>
///   <item><c>{scheme}</c> — case-insensitive. Each plugin owns a literal
///         (<c>applemusic</c>, <c>tidal</c>, etc.).</item>
///   <item><c>:album:</c> — fixed segment. Track-level GUIDs are out of scope for now;
///         callers expecting album extraction get null back when the type segment isn't
///         <c>album</c> (case-insensitive).</item>
///   <item><c>{id}</c> — required, non-empty. Apple uses numeric, tidal uses numeric, qobuz
///         uses arbitrary tokens. The parser doesn't validate format.</item>
///   <item><c>{extra}</c> — optional fourth segment plugins can use for service-specific
///         metadata (e.g. tidal encodes quality here: <c>tidal:album:99999:Lossless</c>).
///         Ignored by this parser.</item>
///   </list>
///
/// <para>The <see cref="ExtractAlbumIdFromUrlPath"/> fallback recognizes any URL whose path
/// contains a <c>/album/{id}/</c> segment.</para>
///
/// <para>Why string params (not <c>ReleaseInfo</c>): Common doesn't reference
/// <c>NzbDrone.Core</c>. Plugins call this with <c>release.Guid</c> and <c>release.InfoUrl</c>
/// extracted at the call site, which lets the helper stay in Common's clean
/// dependency tree (only BCL).</para>
///
/// <para>May 2026 unification — this is Wave A item 3 from
/// <c>memory/project_apple_bridge_unification_plan.md</c>. Apple and Tidal each had ~60 LOC
/// of identical extraction logic with only the scheme literal differing.</para>
/// </summary>
public static class PrefixedReleaseGuidParser
{
    /// <summary>
    /// Convenience: GUID first, fall back to InfoUrl. Returns empty string (not null) so
    /// callers can <c>throw if (string.IsNullOrWhiteSpace(id))</c> without null handling.
    /// </summary>
    public static string ExtractAlbumId(string? guid, string? infoUrl, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix must be non-empty.", nameof(prefix));

        var fromGuid = ExtractAlbumIdFromGuid(guid, prefix);
        if (!string.IsNullOrWhiteSpace(fromGuid))
        {
            return fromGuid;
        }
        return ExtractAlbumIdFromUrlPath(infoUrl) ?? string.Empty;
    }

    /// <summary>
    /// Parse the GUID grammar described in <see cref="PrefixedReleaseGuidParser"/>.
    /// Returns null for any shape that doesn't match (caller should try
    /// <see cref="ExtractAlbumIdFromUrlPath"/> as a fallback).
    /// </summary>
    public static string? ExtractAlbumIdFromGuid(string? guid, string prefix)
    {
        if (string.IsNullOrWhiteSpace(guid)) return null;
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix must be non-empty.", nameof(prefix));

        var normalized = guid;
        var prefixMarker = "_" + prefix + ":";
        var prefixEnd = guid.IndexOf(prefixMarker, StringComparison.OrdinalIgnoreCase);
        if (prefixEnd >= 0)
        {
            normalized = guid[(prefixEnd + 1)..];
        }

        var parts = normalized.Split(':');
        if (parts.Length >= 3 &&
            parts[0].Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("album", StringComparison.OrdinalIgnoreCase))
        {
            // Trim leading/trailing whitespace inside the ID segment: real Lidarr GUIDs
            // are clean, but a tampered source could inject `tidal:album: 99999 ` and the
            // downstream service API would 404 on the un-trimmed value. Cheap insurance.
            var id = parts[2].Trim();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        return null;
    }

    /// <summary>
    /// Find <c>/album/{id}/</c> in a URL's path. Returns null when the URL is empty,
    /// not parseable, or has no <c>album</c> segment.
    /// </summary>
    public static string? ExtractAlbumIdFromUrlPath(string? infoUrl)
    {
        if (string.IsNullOrWhiteSpace(infoUrl)) return null;

        if (!Uri.TryCreate(infoUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var idx = Array.IndexOf(segments, "album");
        if (idx >= 0 && idx < segments.Length - 1)
        {
            var candidate = segments[idx + 1];
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
        }
        return null;
    }
}
