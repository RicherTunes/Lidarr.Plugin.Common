using System;
using Lidarr.Plugin.Abstractions.Models;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Behavioral compliance axis for orchestrator-download plugins (qobuz, tidal, amazon): proves the
/// plugin's download-path <see cref="StreamingAlbum"/> exposes a FETCHABLE cover-art URL via
/// <see cref="StreamingAlbum.GetBestCoverArtUrl"/>, so Common's
/// <c>SimpleDownloadOrchestrator</c> can HTTP GET and embed the album cover into every downloaded
/// file (the art then survives Lidarr import).
///
/// <para>Two real failure modes this axis catches:</para>
/// <list type="bullet">
///   <item><description>CoverArtUrls left empty -> the orchestrator embeds nothing (art-less
///   downloads). This was the qobuz/amazon pre-enabler gap.</description></item>
///   <item><description>CoverArtUrls filled with a raw provider id instead of a URL -> the SSRF-guarded
///   fetch rejects it / fails (the tidal <c>CoverArtId</c> bug).</description></item>
/// </list>
///
/// <para>Apple is intentionally NOT an adopter: its DRM/SDK download path does not route through the
/// orchestrator's cover-fetch seam.</para>
///
/// <para>Adopt by subclassing from the plugin's test project:</para>
/// <code>
/// public sealed class QobuzCoverArtEmbeddingTests : CoverArtEmbeddingComplianceTestBase
/// {
///     protected override StreamingAlbum BuildDownloadPathAlbumWithCover() =>
///         QobuzStreamingTrack.From(sampleTrack, albumWithImages).Album;
///     protected override StreamingAlbum BuildDownloadPathAlbumWithoutCover() =>
///         QobuzStreamingTrack.From(sampleTrack, albumWithoutImages).Album;
/// }
/// </code>
/// </summary>
public abstract class CoverArtEmbeddingComplianceTestBase
{
    /// <summary>
    /// Build the download-path album (as fed to the orchestrator) from a fixture where the provider
    /// DOES supply cover art. <see cref="StreamingAlbum.CoverArtUrls"/> must be populated with
    /// fetchable image URL(s).
    /// </summary>
    protected abstract StreamingAlbum BuildDownloadPathAlbumWithCover();

    /// <summary>
    /// Build the download-path album from a fixture where the provider supplies NO cover art.
    /// <see cref="StreamingAlbum.GetBestCoverArtUrl"/> must degrade to empty without throwing.
    /// </summary>
    protected abstract StreamingAlbum BuildDownloadPathAlbumWithoutCover();

    /// <summary>
    /// True when <paramref name="url"/> is a fetchable absolute http(s) URL -- something the
    /// orchestrator's SSRF-guarded cover fetch can actually retrieve. A raw provider id
    /// ("1a2b-3c4d") or an empty string is NOT fetchable.
    /// </summary>
    protected static bool IsFetchableCoverUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// When the provider supplies cover art, the download-path album must expose it as a fetchable
    /// absolute URL via <see cref="StreamingAlbum.GetBestCoverArtUrl"/> -- otherwise the orchestrator
    /// embeds nothing (art-less downloads) or tries to fetch a raw id (tidal CoverArtId bug).
    /// </summary>
    [Fact]
    public void CoverArt_IsFetchableUrl_WhenProviderSuppliesIt()
    {
        var album = BuildDownloadPathAlbumWithCover();
        Assert.True(album is not null, "BuildDownloadPathAlbumWithCover returned null.");
        Assert.True(
            album!.CoverArtUrls is { Count: > 0 },
            "download-path album has an empty CoverArtUrls -- the plugin must populate it from the " +
            "provider's cover art so the orchestrator can embed the album cover (art-less-downloads gap).");

        var best = album.GetBestCoverArtUrl();
        Assert.True(
            IsFetchableCoverUrl(best),
            $"download-path album's GetBestCoverArtUrl() returned '{best}', which is not a fetchable " +
            "absolute http(s) URL. SimpleDownloadOrchestrator can only embed a cover it can HTTP GET -- " +
            "populate CoverArtUrls with real image URLs, not raw provider ids or empty strings.");
    }

    /// <summary>
    /// When the provider has no cover art, <see cref="StreamingAlbum.GetBestCoverArtUrl"/> must degrade
    /// to empty without throwing -- the orchestrator simply skips embedding.
    /// </summary>
    [Fact]
    public void CoverArt_DegradesToEmpty_WhenProviderHasNone()
    {
        var album = BuildDownloadPathAlbumWithoutCover();
        Assert.True(album is not null, "BuildDownloadPathAlbumWithoutCover returned null.");

        var best = album!.GetBestCoverArtUrl();
        Assert.True(
            string.IsNullOrEmpty(best),
            $"download-path album without provider cover art returned '{best}' from GetBestCoverArtUrl(); " +
            "it must degrade to empty so the orchestrator skips embedding cleanly.");
    }
}
