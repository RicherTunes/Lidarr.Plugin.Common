using System;
using System.Web;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Build + parse <c>{scheme}://search?query={encoded}</c> placeholder URIs.
///
/// <para>Lidarr-native bridge plugins (apple, tidal) use this trick to get a search query
/// through Lidarr's <c>HttpIndexerBase</c> pipeline. The base class expects an HTTP request
/// to be issued in <c>GetRequestGenerator</c>; the actual search has to happen
/// asynchronously in <c>FetchReleases</c>. The compromise is a placeholder
/// <c>HttpRequest</c> whose URL encodes the query, and which the FetchReleases override
/// unwraps when iterating the request chain.</para>
///
/// <para>Identical algorithm in apple's <c>AppleMusicLidarrIndexer.TryExtractQuery</c> and
/// tidal's <c>TidalLidarrIndexer.TryExtractQuery</c> — lifted as Wave A item 5 from
/// <c>memory/project_apple_bridge_unification_plan.md</c>.</para>
/// </summary>
public static class PlaceholderSearchUri
{
    /// <summary>
    /// Build <c>{scheme}://search?query={URL-encoded query}</c>. Caller is responsible for
    /// validating the query first; the builder is defensive (empty query produces a
    /// shape-valid URI with empty query parameter — easier than throwing).
    /// </summary>
    public static string Build(string scheme, string query)
    {
        if (string.IsNullOrWhiteSpace(scheme))
            throw new ArgumentException("Scheme must be non-empty.", nameof(scheme));
        return $"{scheme}://search?query={Uri.EscapeDataString(query ?? string.Empty)}";
    }

    /// <summary>
    /// Extract the query parameter from a placeholder URI. Returns false (with
    /// <paramref name="query"/> set to empty) for any URL that doesn't match
    /// <c>{scheme}://search?query=...</c> with a non-empty query value.
    /// </summary>
    public static bool TryExtractQuery(string url, string scheme, out string query)
    {
        query = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(scheme))
        {
            return false;
        }

        var expectedPrefix = scheme + "://search";
        if (!url.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var raw = HttpUtility.ParseQueryString(uri.Query)["query"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        query = raw;
        return true;
    }
}
