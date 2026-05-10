using Lidarr.Plugin.Common.Services.Intelligence;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Intelligence;

[Trait("Category", "Unit")]
public class LiveAlbumNormalizerTests
{
    private readonly LiveAlbumNormalizer _sut = new();

    [Theory]
    [InlineData("Live at Madison Square Garden")]
    [InlineData("Live At The O2")]
    [InlineData("Recorded Live in Berlin")]
    [InlineData("Live from BBC Studios")]
    [InlineData("Concert Recording")]
    [InlineData("MTV Unplugged")]
    [InlineData("Acoustic Session")]
    [InlineData("BBC Session")]
    public void IsLiveAlbum_DetectsCommonLivePhrasings(string title)
    {
        Assert.True(_sut.IsLiveAlbum(title));
    }

    [Theory]
    [InlineData("Liver")] // "live" embedded in another word should not match (word-boundary check).
    [InlineData("Concerto in C")] // "concert" embedded inside another word should not match.
    [InlineData("Studio Album")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsLiveAlbum_GuardsAgainstSubstringFalsePositives(string title)
    {
        Assert.False(_sut.IsLiveAlbum(title));
    }

    [Fact]
    public void IsLiveAlbum_LiveAsWordIsLive_ConservativeFuzzy()
    {
        // "Live wire" is technically a studio track title, but the live-marker heuristic
        // operates on whole-word "live" match — by design we err on the side of recall here.
        // The downstream Compilation/Live similarity logic uses additional venue/date signals
        // before declaring two titles "the same live album".
        Assert.True(_sut.IsLiveAlbum("Live wire"));
    }

    [Fact]
    public void IsLiveAlbum_HandlesNullInput()
    {
        Assert.False(_sut.IsLiveAlbum(null));
    }

    [Fact]
    public void NormalizeLiveAlbum_ExtractsCoreTitle_FromParenthesesPattern()
    {
        var result = _sut.NormalizeLiveAlbum("Album X (Live at Wembley, 2022)");

        Assert.True(result.IsLiveAlbum);
        Assert.Equal("Album X", result.CoreAlbumTitle);
        Assert.Equal("wembley", result.NormalizedVenue);
    }

    [Fact]
    public void NormalizeLiveAlbum_ExtractsCoreTitle_FromDashPattern()
    {
        var result = _sut.NormalizeLiveAlbum("Album X - Live at Red Rocks");

        Assert.True(result.IsLiveAlbum);
        Assert.Equal("Album X", result.CoreAlbumTitle);
        Assert.Equal("red rocks", result.NormalizedVenue);
    }

    [Fact]
    public void NormalizeLiveAlbum_ExtractsCoreTitle_FromVenueColonPattern()
    {
        var result = _sut.NormalizeLiveAlbum("Live at Carnegie Hall: The Great Concert");

        Assert.True(result.IsLiveAlbum);
        Assert.Equal("The Great Concert", result.CoreAlbumTitle);
        Assert.Equal("carnegie hall", result.NormalizedVenue);
    }

    [Fact]
    public void NormalizeLiveAlbum_StripsTrailingLiveSuffix()
    {
        var result = _sut.NormalizeLiveAlbum("Greatest Hits Live");

        Assert.True(result.IsLiveAlbum);
        Assert.Equal("Greatest Hits", result.CoreAlbumTitle);
    }

    [Fact]
    public void NormalizeLiveAlbum_ReturnsUnchangedForNonLiveTitle()
    {
        var result = _sut.NormalizeLiveAlbum("OK Computer");

        Assert.False(result.IsLiveAlbum);
        Assert.Equal("OK Computer", result.NormalizedTitle);
    }

    [Fact]
    public void NormalizeLiveAlbum_DetectsSpecialSession_MTVUnplugged()
    {
        var result = _sut.NormalizeLiveAlbum("MTV Unplugged: The Album");

        Assert.True(result.IsLiveAlbum);
        Assert.True(result.IsSpecialSession);
        Assert.Equal("MTV Unplugged", result.SpecialContext);
    }

    [Fact]
    public void NormalizeLiveAlbum_NormalizesVenueAliases_MSG()
    {
        var result = _sut.NormalizeLiveAlbum("Album X (Live at MSG, 2019)");

        Assert.Equal("madison square garden", result.NormalizedVenue);
    }

    [Fact]
    public void NormalizeLiveAlbum_NormalizesDate_ToYearOnly()
    {
        var result = _sut.NormalizeLiveAlbum("Album X (Live at MSG, December 31, 2019)");

        Assert.Equal("2019", result.NormalizedDate);
    }

    [Fact]
    public void NormalizeLiveAlbum_GeneratesTitleVariations()
    {
        var result = _sut.NormalizeLiveAlbum("Best Of Live");

        Assert.Contains("Best Of (Live)", result.TitleVariations);
        Assert.Contains("Best Of - Live", result.TitleVariations);
        Assert.Contains("Live: Best Of", result.TitleVariations);
    }

    [Fact]
    public void CalculateLiveAlbumSimilarity_ReturnsHigh_ForSameAlbumDifferentVenuePhrasing()
    {
        var s = _sut.CalculateLiveAlbumSimilarity(
            "OK Computer (Live at Wembley)",
            "OK Computer (Recorded at Wembley Stadium)");

        Assert.True(s > 0.7, $"Expected high similarity, got {s}");
    }

    [Fact]
    public void CalculateLiveAlbumSimilarity_ReturnsOne_ForIdentical()
    {
        var s = _sut.CalculateLiveAlbumSimilarity("Album X (Live)", "Album X (Live)");
        Assert.Equal(1.0, s, 1);
    }

    [Fact]
    public void CalculateLiveAlbumSimilarity_HandlesNullsGracefully()
    {
        Assert.Equal(1.0, _sut.CalculateLiveAlbumSimilarity(null, null));
        Assert.Equal(0.0, _sut.CalculateLiveAlbumSimilarity(null, "x"));
        Assert.Equal(0.0, _sut.CalculateLiveAlbumSimilarity("x", null));
    }

    [Fact]
    public void NormalizeLiveAlbum_HandlesEmptyTitle()
    {
        var result = _sut.NormalizeLiveAlbum("");

        Assert.False(result.IsLiveAlbum);
        Assert.Equal(string.Empty, result.NormalizedTitle);
    }

    [Fact]
    public void NormalizeLiveAlbum_CoreTitleOnlyOption_StripsLiveContext()
    {
        var result = _sut.NormalizeLiveAlbum(
            "Album X (Live at Wembley, 2022)",
            LiveAlbumNormalizationOptions.CoreTitleOnly);

        Assert.Equal("Album X", result.NormalizedTitle);
    }

    [Fact]
    public void NormalizeLiveAlbum_FullContextOption_IncludesVenue()
    {
        var result = _sut.NormalizeLiveAlbum(
            "Album X (Live at MSG)",
            LiveAlbumNormalizationOptions.FullContext);

        Assert.Contains("madison square garden", result.NormalizedTitle ?? "");
    }
}
