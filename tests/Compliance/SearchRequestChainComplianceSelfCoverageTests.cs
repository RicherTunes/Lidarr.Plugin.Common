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

    // ── F03: RequiresExactPlanSequence strict-mode self-coverage ──────────────────────────────

    // Full-chain subclass — opts in to exact sequence enforcement.
    private sealed class StrictGoodChain : SearchRequestChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override bool RequiresExactPlanSequence => true;
        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album) =>
            CompleteChain(artist, album);
    }

    // Duplicates the first planned variant (emits it twice in sequence).
    // The default set-based checks PASS (all chain queries are in the plan; all plan variants are
    // in the chain set). Only the exact-sequence assertion can see the duplicate.
    private sealed class DuplicatesFirstVariant : SearchRequestChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override bool RequiresExactPlanSequence => true;
        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album)
        {
            var list = CompleteChain(artist, album);
            if (list.Count > 0)
            {
                list.Insert(1, list[0]); // emit the first variant twice
            }

            return list;
        }
    }

    // Swaps the two variants after position 0, leaving position 0 intact.
    // The default set-based checks PASS: FirstRequest_IsTheCombinedTierFirstVariant sees chain[0]
    // which is still the correct leading variant; the set-based presence check sees all plan
    // variants present. Only the exact-sequence assertion detects the reordering.
    private sealed class ReordersAfterFirst : SearchRequestChainComplianceTestBase
    {
        protected override string PlaceholderScheme => Scheme;
        protected override bool RequiresExactPlanSequence => true;
        protected override IReadOnlyList<string> GetSearchRequestUrls(string artist, string album)
        {
            var list = CompleteChain(artist, album);
            if (list.Count >= 3)
            {
                // Swap positions 1 and 2: keeps position 0 correct, reorders the rest.
                (list[1], list[2]) = (list[2], list[1]);
            }

            return list;
        }
    }

    /// <summary>Strict mode passes on a correct complete ordered chain.</summary>
    [Fact]
    public void StrictMode_passes_on_a_complete_ordered_chain()
    {
        var strict = new StrictGoodChain();
        strict.EveryRequest_IsAWellFormedPlaceholderUri();
        strict.FirstRequest_IsTheCombinedTierFirstVariant();
        strict.Chain_ContainsOnlyBuildPlanVariants();
        strict.Chain_PreservesEveryBuildPlanVariant_IncludingArtistOnlyFallback();
        strict.Chain_MatchesExactPlanSequence();
    }

    /// <summary>
    /// Old set-based checks PASS on a chain with a duplicate variant;
    /// the new exact-sequence assertion FAILS.
    /// </summary>
    [Fact]
    public void StrictMode_catches_duplicate_planned_variant()
    {
        var dup = new DuplicatesFirstVariant();

        // Verify the OLD checks are blind to the duplicate (they must not throw).
        dup.EveryRequest_IsAWellFormedPlaceholderUri();
        dup.FirstRequest_IsTheCombinedTierFirstVariant();
        dup.Chain_ContainsOnlyBuildPlanVariants();
        dup.Chain_PreservesEveryBuildPlanVariant_IncludingArtistOnlyFallback();

        // The NEW exact-sequence check MUST reject it.
        Assert.ThrowsAny<Exception>(() => dup.Chain_MatchesExactPlanSequence());
    }

    /// <summary>
    /// Old set-based checks PASS on a chain with variants reordered after position 0;
    /// the new exact-sequence assertion FAILS.
    /// </summary>
    [Fact]
    public void StrictMode_catches_reordered_variants_after_first()
    {
        var reordered = new ReordersAfterFirst();

        // Verify the OLD checks are blind to the reordering (they must not throw).
        reordered.EveryRequest_IsAWellFormedPlaceholderUri();
        reordered.FirstRequest_IsTheCombinedTierFirstVariant();
        reordered.Chain_ContainsOnlyBuildPlanVariants();
        reordered.Chain_PreservesEveryBuildPlanVariant_IncludingArtistOnlyFallback();

        // The NEW exact-sequence check MUST reject it.
        Assert.ThrowsAny<Exception>(() => reordered.Chain_MatchesExactPlanSequence());
    }
}
