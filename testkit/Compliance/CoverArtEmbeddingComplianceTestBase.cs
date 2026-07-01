using System;
using System.Net;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Services.Download;
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

    // A public IP so the guard's DNS/private-IP leg passes for a hostname fixture without a real DNS
    // lookup (hermetic). IP-literal hosts are still checked directly by the guard, so a private-IP
    // literal is still rejected -- only real hostname resolution is stubbed.
    private static readonly Func<string, IPAddress[]> PublicHostResolver =
        _ => new[] { IPAddress.Parse("93.184.216.34") };

    /// <summary>
    /// True when <paramref name="url"/> is a cover URL the orchestrator can actually fetch. Delegates
    /// to the SAME <see cref="RemoteMediaUriGuard"/> + <see cref="RemoteMediaUriPolicy.Strict"/> the
    /// orchestrator applies, so a URL that passes here is exactly one the orchestrator would embed --
    /// no "test green, orchestrator silently skips" divergence. This rejects plain http:// (Strict is
    /// https-only), userinfo, metadata hosts, and private-IP literals -- all of which a naive
    /// "any absolute http(s) URL" check would wrongly accept.
    /// </summary>
    protected static bool IsFetchableCoverUrl(string? url) =>
        RemoteMediaUriGuard.Validate(url, RemoteMediaUriPolicy.Strict, PublicHostResolver).IsAllowed;

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
            string.IsNullOrWhiteSpace(best),
            $"download-path album without provider cover art returned '{best}' from GetBestCoverArtUrl(); " +
            "it must degrade to empty/blank so the orchestrator (which skips on IsNullOrWhiteSpace) embeds nothing.");
    }
}
