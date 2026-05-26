using System;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Build + parse <c>{scheme}://album/{id}[?quality={q}]</c> placeholder URIs.
///
/// <para>Every streaming plugin emits an album-download URI as the
/// <c>ReleaseInfo.DownloadUrl</c> when an indexer returns a release. The
/// download client later parses that URL back to recover the album id (and
/// optionally a quality hint) so it can call the streaming-service API. This
/// helper keeps the grammar + parse round-trip in one place — siblings of
/// <see cref="PlaceholderSearchUri"/> (search URLs) and
/// <see cref="PrefixedReleaseGuidParser"/> (release GUID grammar).</para>
///
/// <para>Supported parse formats (parser is liberal so legacy releases continue
/// to resolve after a plugin migrates):</para>
/// <list type="bullet">
///   <item>New: <c>{scheme}://album/{id}?quality={q}</c></item>
///   <item>New (no quality): <c>{scheme}://album/{id}</c></item>
///   <item>Legacy path-segment quality: <c>{scheme}://album/{id}/{q}</c></item>
/// </list>
///
/// <para>The <see cref="Build"/> emitter always uses the NEW format
/// (<c>?quality=</c> query parameter). Legacy parsing exists only to drain
/// in-flight downloads queued before a plugin migration.</para>
///
/// <para>Originally implemented in
/// <c>qobuzarr/src/Download/Services/AlbumIdExtractor.ExtractAlbumIdFromDownloadUrl</c>.
/// Lifted as Wave 19B so the apple/tidalarr download clients can adopt the same
/// parser when their respective release-URL handling consolidates.</para>
/// </summary>
public static class AlbumDownloadUri
{
    /// <summary>
    /// Build <c>{scheme}://album/{id}[?quality={q}]</c>. Quality is appended only
    /// when non-empty.
    /// </summary>
    public static string Build(string scheme, string albumId, string? quality = null)
    {
        if (string.IsNullOrWhiteSpace(scheme))
            throw new ArgumentException("Scheme must be non-empty.", nameof(scheme));
        if (string.IsNullOrWhiteSpace(albumId))
            throw new ArgumentException("Album id must be non-empty.", nameof(albumId));

        var trimmedScheme = scheme.Trim();
        var trimmedId = albumId.Trim();
        var trimmedQuality = quality?.Trim();

        return string.IsNullOrEmpty(trimmedQuality)
            ? $"{trimmedScheme}://album/{Uri.EscapeDataString(trimmedId)}"
            : $"{trimmedScheme}://album/{Uri.EscapeDataString(trimmedId)}?quality={Uri.EscapeDataString(trimmedQuality)}";
    }

    /// <summary>
    /// Extract the album id from a download URL emitted by <see cref="Build"/> (or
    /// from the legacy path-segment-quality format). Returns false (with
    /// <paramref name="albumId"/> set to empty) for any URL that doesn't match
    /// the <c>{scheme}://album/...</c> shape.
    ///
    /// <para>The album id is URL-decoded before being returned (so callers don't
    /// need to handle <c>%</c>-escapes from the wire).</para>
    /// </summary>
    public static bool TryExtractAlbumId(string? url, string scheme, out string albumId)
    {
        albumId = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(scheme))
        {
            return false;
        }

        var expectedPrefix = scheme + "://album/";
        if (!url.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var afterPrefix = url.Substring(expectedPrefix.Length);
        // Format A: "{id}?quality={q}" — strip the query string.
        var queryIdx = afterPrefix.IndexOf('?');
        if (queryIdx >= 0)
        {
            var idPart = afterPrefix.Substring(0, queryIdx);
            if (string.IsNullOrWhiteSpace(idPart)) return false;
            albumId = Uri.UnescapeDataString(idPart);
            return true;
        }

        // Format B (legacy): "{id}/{quality}" — last segment is quality.
        var lastSlash = afterPrefix.LastIndexOf('/');
        if (lastSlash > 0)
        {
            var idPart = afterPrefix.Substring(0, lastSlash);
            if (string.IsNullOrWhiteSpace(idPart)) return false;
            albumId = Uri.UnescapeDataString(idPart);
            return true;
        }

        // Format C: "{id}" — bare, no query, no path-segment quality.
        if (string.IsNullOrWhiteSpace(afterPrefix)) return false;
        albumId = Uri.UnescapeDataString(afterPrefix);
        return true;
    }
}
