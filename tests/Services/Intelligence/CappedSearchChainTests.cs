using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.Services.Intelligence;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Intelligence;

/// <summary>
/// Contract suite for <see cref="CappedSearchChain"/> — the reusable "cap the over-specific queries but
/// ALWAYS preserve the artist-only catalogue fallback" policy lifted from qobuz's bespoke
/// <c>QobuzRequestGenerator.CreateIndexerRequests</c>. The fallback-survival facts pin the regression the
/// policy exists to prevent: the shipped "Bleu Jeans Bleu - Record n°V" bug, where a special-char album
/// query returned 0 results while the artist-only fallback had been truncated away by the request cap.
/// </summary>
public sealed class CappedSearchChainTests
{
    [Fact]
    public void CapsOverSpecificQueries_ToMax()
    {
        var result = CappedSearchChain.Build(
            new[] { "q1", "q2", "q3", "q4", "q5" }, artistOnlyFallback: null, maxOverSpecific: 3);

        Assert.Equal(new[] { "q1", "q2", "q3" }, result);
    }

    [Fact]
    public void AlwaysAppendsArtistOnlyFallback_EvenWhenOverSpecificExceedsCap()
    {
        // THE guarantee: over-specific queries are capped to 3, but the artist-only fallback is issued IN
        // ADDITION (never truncated) so an over-specific/special-char query can always degrade to the
        // band's catalogue. Regression guard for "Bleu Jeans Bleu - Record n°V".
        var result = CappedSearchChain.Build(
            new[] { "q1", "q2", "q3", "q4", "q5" }, artistOnlyFallback: "Bleu Jeans Bleu", maxOverSpecific: 3);

        Assert.Equal(new[] { "q1", "q2", "q3", "Bleu Jeans Bleu" }, result);
        Assert.Equal("Bleu Jeans Bleu", result.Last());
    }

    [Fact]
    public void AlwaysAppendsArtistOnlyFallbackVariants_EvenWhenOverSpecificExceedsCap()
    {
        var result = CappedSearchChain.Build(
            new[] { "q1", "q2", "q3", "q4" },
            artistOnlyFallbacks: new[] { "AC/DC", "AC DC", "ACDC" },
            maxOverSpecific: 2);

        Assert.Equal(new[] { "q1", "q2", "AC/DC", "AC DC", "ACDC" }, result);
    }

    [Fact]
    public void DropsBlankAndWhitespaceQueries_PreservingOrder()
    {
        var result = CappedSearchChain.Build(
            new[] { "q1", "", "  ", "q2", null! }, artistOnlyFallback: null, maxOverSpecific: 5);

        Assert.Equal(new[] { "q1", "q2" }, result);
    }

    [Fact]
    public void DedupsOverSpecificQueries_CaseInsensitively_PreservingFirst()
    {
        var result = CappedSearchChain.Build(
            new[] { "Artist Album", "artist album", "ARTIST ALBUM", "Other" }, artistOnlyFallback: null, maxOverSpecific: 5);

        Assert.Equal(new[] { "Artist Album", "Other" }, result);
    }

    [Fact]
    public void NullOrBlankFallback_IsNotAppended()
    {
        Assert.Equal(new[] { "q1" }, CappedSearchChain.Build(new[] { "q1" }, artistOnlyFallback: null, maxOverSpecific: 3));
        Assert.Equal(new[] { "q1" }, CappedSearchChain.Build(new[] { "q1" }, artistOnlyFallback: "   ", maxOverSpecific: 3));
    }

    [Fact]
    public void FallbackAlreadyInCappedSet_IsNotDuplicated_CaseInsensitive()
    {
        var result = CappedSearchChain.Build(
            new[] { "Radiohead", "Radiohead OK Computer" }, artistOnlyFallback: "radiohead", maxOverSpecific: 3);

        Assert.Equal(new[] { "Radiohead", "Radiohead OK Computer" }, result);
    }

    [Fact]
    public void FallbackEqualToATruncatedQuery_IsStillAppended()
    {
        // The dup check is against the CAPPED set only — a fallback equal to a query that fell beyond the
        // cap is still issued (matches qobuz's selected.Contains semantics).
        var result = CappedSearchChain.Build(
            new[] { "q1", "q2", "q3", "TheBand" }, artistOnlyFallback: "TheBand", maxOverSpecific: 3);

        Assert.Equal(new[] { "q1", "q2", "q3", "TheBand" }, result);
    }

    [Fact]
    public void NullOrEmptyOverSpecific_ReturnsFallbackOnly()
    {
        Assert.Equal(new[] { "TheBand" }, CappedSearchChain.Build(null!, "TheBand", maxOverSpecific: 3));
        Assert.Empty(CappedSearchChain.Build(new string[0], artistOnlyFallback: null, maxOverSpecific: 3));
    }

    [Fact]
    public void MaxZeroOrNegative_ReturnsFallbackOnly()
    {
        Assert.Equal(new[] { "TheBand" }, CappedSearchChain.Build(new[] { "q1", "q2" }, "TheBand", maxOverSpecific: 0));
        Assert.Empty(CappedSearchChain.Build(new[] { "q1", "q2" }, artistOnlyFallback: null, maxOverSpecific: -5));
    }
}
