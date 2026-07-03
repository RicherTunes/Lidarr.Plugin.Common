using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.HostBridge;
using Lidarr.Plugin.Common.Services.Intelligence;
using Lidarr.Plugin.Common.TestKit.Compliance;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Compliance;

/// <summary>
/// Self-coverage for <see cref="CappedSearchChainComplianceTestBase"/>: proves the guard PASSES on a
/// correct capped chain (cap respected, artist-only fallback present) and CATCHES each violation
/// (dropped artist-only fallback, exceeded cap) -- before any plugin adopts the axis.
/// </summary>
public class CappedSearchChainComplianceSelfCoverageTests
{
    private const string Scheme = "cappedcov";
    private const int Cap = 2;

    // Correct: CappedSearchChain.Build with cap=2 -- emits 2 over-specific + artist-only fallback.
    private sealed class CorrectCappedChain : CappedSearchChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override int MaxOverSpecificQueries => Cap;
        protected override string GetExpectedArtistOnlyFallbackQuery(string artist, string album) => artist;

        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album)
        {
            var overSpecific = new[] { $"{artist} {album}", $"{album} {artist}", $"extra1 {artist}", $"extra2 {artist}" };
            var queries = CappedSearchChain.Build(overSpecific, artistOnlyFallback: artist, maxOverSpecific: Cap);
            return queries.Select(q => PlaceholderSearchUri.Build(Scheme, q)).ToList();
        }
    }

    // Broken: passes null fallback -- the artist-only query never reaches the chain.
    private sealed class DropsArtistOnlyFallback : CappedSearchChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override int MaxOverSpecificQueries => Cap;
        protected override string GetExpectedArtistOnlyFallbackQuery(string artist, string album) => artist;

        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album)
        {
            var overSpecific = new[] { $"{artist} {album}", $"{album} {artist}" };
            var queries = CappedSearchChain.Build(overSpecific, artistOnlyFallback: null, maxOverSpecific: Cap);
            return queries.Select(q => PlaceholderSearchUri.Build(Scheme, q)).ToList();
        }
    }

    // Broken: preserves only the exact artist fallback and drops the rest of the artist-only fallback tier.
    private sealed class DropsSecondaryArtistOnlyFallback : CappedSearchChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override int MaxOverSpecificQueries => Cap;
        protected override string GetExpectedArtistOnlyFallbackQuery(string artist, string album) => artist;
        protected override IReadOnlyList<string> GetExpectedArtistOnlyFallbackQueries(string artist, string album) =>
            new[] { artist, $"{artist} folded" };

        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album)
        {
            var overSpecific = new[] { $"{artist} {album}", $"{album} {artist}" };
            var queries = CappedSearchChain.Build(overSpecific, artistOnlyFallback: artist, maxOverSpecific: Cap);
            return queries.Select(q => PlaceholderSearchUri.Build(Scheme, q)).ToList();
        }
    }

    // Broken: issues 3 over-specific queries bypassing CappedSearchChain.Build, exceeding cap=2.
    private sealed class ExceedsCap : CappedSearchChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override int MaxOverSpecificQueries => Cap;
        protected override string GetExpectedArtistOnlyFallbackQuery(string artist, string album) => artist;

        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album)
        {
            var queries = new[] { $"{artist} {album}", $"{album} {artist}", $"extra {artist}", artist };
            return queries.Select(q => PlaceholderSearchUri.Build(Scheme, q)).ToList();
        }
    }

    [Fact]
    public void Base_passes_on_correct_capped_chain()
    {
        var good = new CorrectCappedChain();
        good.OverSpecificQueryCount_DoesNotExceedCap();
        good.ArtistOnlyFallback_IsAlwaysPresent();
    }

    [Fact]
    public void Base_catches_dropped_artist_only_fallback()
        => Assert.ThrowsAny<Exception>(() =>
            new DropsArtistOnlyFallback().ArtistOnlyFallback_IsAlwaysPresent());

    [Fact]
    public void Base_catches_dropped_artist_only_fallback_variant()
        => Assert.ThrowsAny<Exception>(() =>
            new DropsSecondaryArtistOnlyFallback().ArtistOnlyFallback_IsAlwaysPresent());

    [Fact]
    public void Base_catches_exceeded_cap()
        => Assert.ThrowsAny<Exception>(() =>
            new ExceedsCap().OverSpecificQueryCount_DoesNotExceedCap());
}
