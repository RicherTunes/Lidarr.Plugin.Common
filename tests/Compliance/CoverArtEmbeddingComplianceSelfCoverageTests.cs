using System;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.TestKit.Compliance;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Compliance;

/// <summary>
/// Self-coverage for <see cref="CoverArtEmbeddingComplianceTestBase"/>: proves the guard PASSES on a
/// download-path album that carries real cover-art URLs and CATCHES the two failure modes -- empty
/// CoverArtUrls (art-less downloads) and a raw provider id in place of a URL (the tidal CoverArtId
/// bug) -- before any plugin adopts the axis.
/// </summary>
public class CoverArtEmbeddingComplianceSelfCoverageTests
{
    private static StreamingAlbum AlbumWith(params (string size, string url)[] covers)
    {
        var album = new StreamingAlbum { Id = "a", Title = "t" };
        foreach (var (size, url) in covers)
        {
            album.CoverArtUrls[size] = url;
        }

        return album;
    }

    // Correct: real absolute image URLs -- the orchestrator can HTTP GET and embed them.
    private sealed class CorrectAlbum : CoverArtEmbeddingComplianceTestBase
    {
        protected override StreamingAlbum BuildDownloadPathAlbumWithCover() =>
            AlbumWith(("large", "https://cdn.example.com/cover/large.jpg"));

        protected override StreamingAlbum BuildDownloadPathAlbumWithoutCover() => AlbumWith();
    }

    // Broken: CoverArtUrls left empty -> art-less downloads (the qobuz/amazon pre-enabler gap).
    private sealed class EmptyCoverAlbum : CoverArtEmbeddingComplianceTestBase
    {
        protected override StreamingAlbum BuildDownloadPathAlbumWithCover() => AlbumWith();

        protected override StreamingAlbum BuildDownloadPathAlbumWithoutCover() => AlbumWith();
    }

    // Broken: raw provider id instead of a URL (the tidal CoverArtId bug the orchestrator can't fetch).
    private sealed class RawIdAlbum : CoverArtEmbeddingComplianceTestBase
    {
        protected override StreamingAlbum BuildDownloadPathAlbumWithCover() =>
            AlbumWith(("large", "1a2b3c4d-5e6f"));

        protected override StreamingAlbum BuildDownloadPathAlbumWithoutCover() => AlbumWith();
    }

    // Broken: a plain http:// URL. RemoteMediaUriPolicy.Strict is https-only, so the orchestrator
    // silently skips it -- a naive "any absolute http(s) URL" predicate would wrongly pass this.
    private sealed class HttpNotHttpsAlbum : CoverArtEmbeddingComplianceTestBase
    {
        protected override StreamingAlbum BuildDownloadPathAlbumWithCover() =>
            AlbumWith(("large", "http://cdn.example.com/cover/large.jpg"));

        protected override StreamingAlbum BuildDownloadPathAlbumWithoutCover() => AlbumWith();
    }

    // Broken: the "no cover" fixture wrongly returns a populated URL -- the degradation invariant
    // must catch an adopter that can't actually produce a coverless album.
    private sealed class PopulatedWhenShouldBeEmpty : CoverArtEmbeddingComplianceTestBase
    {
        protected override StreamingAlbum BuildDownloadPathAlbumWithCover() =>
            AlbumWith(("large", "https://cdn.example.com/cover/large.jpg"));

        protected override StreamingAlbum BuildDownloadPathAlbumWithoutCover() =>
            AlbumWith(("large", "https://cdn.example.com/cover/large.jpg"));
    }

    [Fact]
    public void Base_passes_on_real_cover_urls()
    {
        var good = new CorrectAlbum();
        good.CoverArt_IsFetchableUrl_WhenProviderSuppliesIt();
        good.CoverArt_DegradesToEmpty_WhenProviderHasNone();
    }

    [Fact]
    public void Base_catches_empty_cover_urls()
        => Assert.ThrowsAny<Exception>(() =>
            new EmptyCoverAlbum().CoverArt_IsFetchableUrl_WhenProviderSuppliesIt());

    [Fact]
    public void Base_catches_raw_id_instead_of_url()
        => Assert.ThrowsAny<Exception>(() =>
            new RawIdAlbum().CoverArt_IsFetchableUrl_WhenProviderSuppliesIt());

    [Fact]
    public void Base_catches_plain_http_url_that_strict_policy_rejects()
        => Assert.ThrowsAny<Exception>(() =>
            new HttpNotHttpsAlbum().CoverArt_IsFetchableUrl_WhenProviderSuppliesIt());

    [Fact]
    public void Base_catches_populated_url_in_without_cover_fixture()
        => Assert.ThrowsAny<Exception>(() =>
            new PopulatedWhenShouldBeEmpty().CoverArt_DegradesToEmpty_WhenProviderHasNone());
}
