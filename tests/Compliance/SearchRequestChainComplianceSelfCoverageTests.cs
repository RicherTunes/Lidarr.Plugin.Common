using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.HostBridge;
using Lidarr.Plugin.Common.Services.Intelligence;
using Lidarr.Plugin.Common.TestKit.Compliance;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Compliance;

/// <summary>
/// Self-coverage for <see cref="SearchRequestChainComplianceTestBase"/>: proves the guard PASSES on a
/// complete, sanitized, placeholder-encoded chain and CATCHES each real-world violation (dropped
/// artist-only fallback tier, non-placeholder request, rogue out-of-plan query, wrong leading variant,
/// un-sanitized special-character query) — before any plugin adopts the axis. Fakes are private nested
/// classes so xUnit does not discover them as tests in their own right.
/// </summary>
public class SearchRequestChainComplianceSelfCoverageTests
{
    private const string Scheme = "selfcov";

    private static List<string> CompleteChain(string artist, string album) =>
        SearchQuerySanitizer.BuildPlan(artist, album).Tiers
            .SelectMany(t => t)
            .Select(v => PlaceholderSearchUri.Build(Scheme, v))
            .ToList();

    // A faithful generator: every plan variant, as a placeholder URI, in plan order.
    private sealed class GoodChain : SearchRequestChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album) =>
            CompleteChain(artist, album);
    }

    // Drops every tier after the combined tier (the Take(N)/Bleu-Jeans bug).
    private sealed class DropsArtistTier : SearchRequestChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album) =>
            SearchQuerySanitizer.BuildPlan(artist, album).Combined
                .Select(v => PlaceholderSearchUri.Build(Scheme, v))
                .ToList();
    }

    // Emits a raw API URL instead of a placeholder URI.
    private sealed class NotPlaceholder : SearchRequestChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album) =>
            new[] { "https://api.example.com/search?q=" + Uri.EscapeDataString(artist + " " + album) };
    }

    // Injects a query that BuildPlan never produced.
    private sealed class RogueExtra : SearchRequestChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album)
        {
            var list = CompleteChain(artist, album);
            list.Add(PlaceholderSearchUri.Build(Scheme, "rogue injected query"));
            return list;
        }
    }

    // Leads with a non-combined variant.
    private sealed class WrongFirst : SearchRequestChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album)
        {
            var list = CompleteChain(artist, album);
            list.Reverse();
            return list;
        }
    }

    // Skips the sanitizer for the special-character sample (raw ° reaches the placeholder URI).
    private sealed class UnsanitizedSpecial : SearchRequestChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album)
        {
            if (string.Equals(album, SpecialAlbum, StringComparison.Ordinal))
            {
                return new[] { PlaceholderSearchUri.Build(Scheme, artist + " " + album) }; // raw, unsanitized
            }

            return CompleteChain(artist, album);
        }
    }

    [Fact]
    public void Base_passes_on_a_complete_sanitized_chain()
    {
        var good = new GoodChain();
        good.EveryRequest_IsAWellFormedPlaceholderUri();
        good.FirstRequest_IsTheCombinedTierFirstVariant();
        good.Chain_ContainsOnlyBuildPlanVariants();
        good.Chain_PreservesEveryBuildPlanVariant_IncludingArtistOnlyFallback();
        good.SpecialCharInput_IsSanitizedThroughoutTheChain();
    }

    [Fact]
    public void Base_catches_dropped_artist_only_fallback_tier()
        => Assert.ThrowsAny<Exception>(() =>
            new DropsArtistTier().Chain_PreservesEveryBuildPlanVariant_IncludingArtistOnlyFallback());

    [Fact]
    public void Base_catches_non_placeholder_request()
        => Assert.ThrowsAny<Exception>(() =>
            new NotPlaceholder().EveryRequest_IsAWellFormedPlaceholderUri());

    [Fact]
    public void Base_catches_rogue_out_of_plan_query()
        => Assert.ThrowsAny<Exception>(() =>
            new RogueExtra().Chain_ContainsOnlyBuildPlanVariants());

    [Fact]
    public void Base_catches_wrong_leading_variant()
        => Assert.ThrowsAny<Exception>(() =>
            new WrongFirst().FirstRequest_IsTheCombinedTierFirstVariant());

    [Fact]
    public void Base_catches_unsanitized_special_char_query()
        => Assert.ThrowsAny<Exception>(() =>
            new UnsanitizedSpecial().SpecialCharInput_IsSanitizedThroughoutTheChain());
}
