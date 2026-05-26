using System;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// TDD pins for <see cref="AlbumReleaseInfoBuilder"/>. Wave A item 8 from the May 2026
/// bridge-unification plan — Guid/DownloadUrl/Title construction was duplicated ~40 LOC per
/// plugin (tidalarr, applemusicarr) with only scheme + format-marker differing.
///
/// GUID grammar: <c>{scheme}:album:{albumId}[:{qualityHint}]</c>
/// DownloadUrl:  <c>{scheme}://album/{albumId}[?quality={qualityHint}]</c>
/// Title:        <c>{Artist} - {Album} [({Year})] [{FormatMarker}] [{ReleaseGroup}]</c>
/// </summary>
public class AlbumReleaseInfoBuilderTests
{
    private static AlbumReleaseInfoBuilder BaseBuilder() =>
        new AlbumReleaseInfoBuilder()
            .WithArtist("Miles Davis")
            .WithAlbum("Kind of Blue")
            .WithYear(1959)
            .WithFormatMarker("FLAC")
            .WithScheme("tidal")
            .WithAlbumId("12345");

    [Fact]
    public void Build_WithAllFields_ProducesCorrectTitle()
    {
        var (_, _, title) = BaseBuilder().Build();
        Assert.Equal("Miles Davis - Kind of Blue (1959) [FLAC] [WEB]", title);
    }

    [Fact]
    public void Build_WithoutYear_OmitsParens()
    {
        var (_, _, title) = BaseBuilder().WithYear(null).Build();
        Assert.Equal("Miles Davis - Kind of Blue [FLAC] [WEB]", title);
    }

    [Fact]
    public void Build_WithZeroYear_OmitsParens()
    {
        var (_, _, title) = BaseBuilder().WithYear(0).Build();
        Assert.Equal("Miles Davis - Kind of Blue [FLAC] [WEB]", title);
    }

    [Fact]
    public void Build_WithoutFormatMarker_OmitsFormatBracket()
    {
        // Apple Music behaviour — no format marker in the title.
        var (_, _, title) = new AlbumReleaseInfoBuilder()
            .WithArtist("The Beatles")
            .WithAlbum("Abbey Road")
            .WithYear(1969)
            .WithScheme("applemusic")
            .WithAlbumId("1474000000")
            .Build();

        Assert.Equal("The Beatles - Abbey Road (1969) [WEB]", title);
    }

    [Fact]
    public void Build_WithoutFormatMarkerOrYear_ProducesMinimalTitle()
    {
        var (_, _, title) = new AlbumReleaseInfoBuilder()
            .WithArtist("The Beatles")
            .WithAlbum("Abbey Road")
            .WithScheme("applemusic")
            .WithAlbumId("1474000000")
            .Build();

        Assert.Equal("The Beatles - Abbey Road [WEB]", title);
    }

    [Fact]
    public void Build_WithCustomReleaseGroup_UsesGroup()
    {
        var (_, _, title) = BaseBuilder().WithReleaseGroup("HIRES").Build();
        Assert.Equal("Miles Davis - Kind of Blue (1959) [FLAC] [HIRES]", title);
    }

    [Fact]
    public void Build_WithExtraMarker_InsertsBeforeReleaseGroup()
    {
        // Tidal HiRes: [FLAC] [HIRES] [WEB]
        var (_, _, title) = BaseBuilder()
            .WithFormatMarker("FLAC")
            .WithExtraMarker("HIRES")
            .Build();
        Assert.Equal("Miles Davis - Kind of Blue (1959) [FLAC] [HIRES] [WEB]", title);
    }

    [Fact]
    public void Build_WithExtraMarker_NoFormatMarker_SkipsFormatBracketOnly()
    {
        // ExtraMarker without FormatMarker: the format bracket is still omitted.
        var (_, _, title) = new AlbumReleaseInfoBuilder()
            .WithArtist("Artist")
            .WithAlbum("Album")
            .WithYear(2024)
            .WithExtraMarker("BONUS")
            .WithScheme("tidal")
            .WithAlbumId("99")
            .Build();
        Assert.Equal("Artist - Album (2024) [BONUS] [WEB]", title);
    }

    [Fact]
    public void Build_PreservesGuidGrammar()
    {
        var (guid, _, _) = BaseBuilder().Build();
        Assert.Equal("tidal:album:12345", guid);
    }

    [Fact]
    public void Build_PreservesDownloadUrlGrammar()
    {
        var (_, url, _) = BaseBuilder().Build();
        Assert.Equal("tidal://album/12345", url);
    }

    [Fact]
    public void Build_WithQualityHint_AppendsToGuidAndUrl()
    {
        var (guid, url, _) = BaseBuilder().WithQualityHint("Lossless").Build();
        Assert.Equal("tidal:album:12345:Lossless", guid);
        Assert.Equal("tidal://album/12345?quality=Lossless", url);
    }

    [Fact]
    public void Build_WithQualityHint_UrlEncodesHintInDownloadUrl()
    {
        // Quality hints with spaces (e.g. "Hi Res") must be URL-encoded in DownloadUrl
        // but NOT in Guid (colons are grammatical in the GUID grammar).
        var (guid, url, _) = BaseBuilder().WithQualityHint("Hi Res").Build();
        Assert.Equal("tidal:album:12345:Hi Res", guid);
        Assert.Equal("tidal://album/12345?quality=Hi%20Res", url);
    }

    [Fact]
    public void Build_MissingArtist_Throws()
    {
        var builder = new AlbumReleaseInfoBuilder()
            .WithAlbum("Kind of Blue")
            .WithScheme("tidal")
            .WithAlbumId("12345");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_MissingAlbum_Throws()
    {
        var builder = new AlbumReleaseInfoBuilder()
            .WithArtist("Miles Davis")
            .WithScheme("tidal")
            .WithAlbumId("12345");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_MissingScheme_Throws()
    {
        var builder = new AlbumReleaseInfoBuilder()
            .WithArtist("Miles Davis")
            .WithAlbum("Kind of Blue")
            .WithAlbumId("12345");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_MissingAlbumId_Throws()
    {
        var builder = new AlbumReleaseInfoBuilder()
            .WithArtist("Miles Davis")
            .WithAlbum("Kind of Blue")
            .WithScheme("tidal");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_Idempotent_SameInputProducesSameOutput()
    {
        var builder = BaseBuilder().WithQualityHint("Lossless");
        var first = builder.Build();
        var second = builder.Build();
        Assert.Equal(first.Guid, second.Guid);
        Assert.Equal(first.DownloadUrl, second.DownloadUrl);
        Assert.Equal(first.Title, second.Title);
    }

    [Fact]
    public void Build_GuidAlignedWithPrefixedReleaseGuidParser()
    {
        // Verify the GUID produced by the builder can be round-tripped by
        // PrefixedReleaseGuidParser — the consumer that every download client uses.
        var (guid, _, _) = BaseBuilder().Build();
        var extractedId = PrefixedReleaseGuidParser.ExtractAlbumIdFromGuid(guid, "tidal");
        Assert.Equal("12345", extractedId);
    }

    [Fact]
    public void Build_GuidWithQualityHint_AlignedWithPrefixedReleaseGuidParser()
    {
        // The quality hint lives in segment 4 — PrefixedReleaseGuidParser ignores it
        // and still returns the correct album ID.
        var (guid, _, _) = BaseBuilder().WithQualityHint("HiRes").Build();
        var extractedId = PrefixedReleaseGuidParser.ExtractAlbumIdFromGuid(guid, "tidal");
        Assert.Equal("12345", extractedId);
    }

    // ================================================================== //
    // Edition / Explicit / Live bracket slots (Wave 19D)
    // ================================================================== //
    //
    // Qobuzarr's TitleGenerator emits a 5-bracket shape:
    //   {Artist} - {Album} ({Year}) [Edition] [Explicit] [LIVE] [Format] [WEB]
    // These slots sit BEFORE [Format] / [Extra] so adding them is additive — tidalarr/
    // apple builds (which don't set them) produce byte-identical output to today.

    [Fact]
    public void Build_WithEditionMarker_InsertedBeforeFormat()
    {
        var (_, _, title) = BaseBuilder().WithEditionMarker("Deluxe Edition").Build();
        Assert.Equal("Miles Davis - Kind of Blue (1959) [Deluxe Edition] [FLAC] [WEB]", title);
    }

    [Fact]
    public void Build_WithExplicitMarker_InsertedBetweenEditionAndFormat()
    {
        var (_, _, title) = BaseBuilder().WithExplicitMarker(true).Build();
        Assert.Equal("Miles Davis - Kind of Blue (1959) [Explicit] [FLAC] [WEB]", title);
    }

    [Fact]
    public void Build_WithLiveMarker_InsertedBetweenExplicitAndFormat()
    {
        var (_, _, title) = BaseBuilder().WithLiveMarker(true).Build();
        Assert.Equal("Miles Davis - Kind of Blue (1959) [LIVE] [FLAC] [WEB]", title);
    }

    [Fact]
    public void Build_WithEditionExplicitLive_AllInOrderBeforeFormat()
    {
        // Verifies the full qobuzarr shape: Artist - Album (Year) [Edition] [Explicit] [LIVE] [Format] [WEB]
        var (_, _, title) = BaseBuilder()
            .WithEditionMarker("Anniversary Edition")
            .WithExplicitMarker(true)
            .WithLiveMarker(true)
            .Build();
        Assert.Equal(
            "Miles Davis - Kind of Blue (1959) [Anniversary Edition] [Explicit] [LIVE] [FLAC] [WEB]",
            title);
    }

    [Fact]
    public void Build_ExplicitMarkerFalse_Omitted()
    {
        var (_, _, title) = BaseBuilder().WithExplicitMarker(false).Build();
        Assert.Equal("Miles Davis - Kind of Blue (1959) [FLAC] [WEB]", title);
    }

    [Fact]
    public void Build_LiveMarkerFalse_Omitted()
    {
        var (_, _, title) = BaseBuilder().WithLiveMarker(false).Build();
        Assert.Equal("Miles Davis - Kind of Blue (1959) [FLAC] [WEB]", title);
    }

    [Fact]
    public void Build_EditionMarkerNullOrEmpty_Omitted()
    {
        Assert.Equal(
            "Miles Davis - Kind of Blue (1959) [FLAC] [WEB]",
            BaseBuilder().WithEditionMarker(null).Build().Title);
        Assert.Equal(
            "Miles Davis - Kind of Blue (1959) [FLAC] [WEB]",
            BaseBuilder().WithEditionMarker("").Build().Title);
        Assert.Equal(
            "Miles Davis - Kind of Blue (1959) [FLAC] [WEB]",
            BaseBuilder().WithEditionMarker("   ").Build().Title);
    }

    [Fact]
    public void Build_EditionExplicitLiveWithExtraMarker_AllSlotsInOrder()
    {
        // Combined with tidalarr's ExtraMarker pattern — full theoretical shape:
        //   Artist - Album (Year) [Edition] [Explicit] [LIVE] [Format] [Extra] [WEB]
        var (_, _, title) = BaseBuilder()
            .WithEditionMarker("Deluxe")
            .WithExplicitMarker(true)
            .WithLiveMarker(true)
            .WithExtraMarker("HIRES")
            .Build();
        Assert.Equal(
            "Miles Davis - Kind of Blue (1959) [Deluxe] [Explicit] [LIVE] [FLAC] [HIRES] [WEB]",
            title);
    }
}
