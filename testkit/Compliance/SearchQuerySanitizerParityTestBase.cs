using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.Services.Intelligence;
using Lidarr.Plugin.Common.TestKit.Data;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Shared parity suite that runs the FULL tricky-character corpus through a plugin's search-query
/// sanitizer entrypoint and asserts the universal invariants every plugin must hold. A plugin adopts
/// the <c>search-query-sanitizer</c> parity axis in ~10 lines:
///
/// <code>
/// public sealed class TidalSearchQuerySanitizerParityTests : SearchQuerySanitizerParityTestBase
/// {
///     protected override SanitizedQuery SanitizeViaPlugin(string? raw) => SearchQuerySanitizer.Sanitize(raw);
///     protected override SearchPlan BuildPlanViaPlugin(string artist, string album) =>
///         /* the plugin's REAL request-generator path, e.g. */ TidalLidarrRequestGenerator.BuildPlanForTest(artist, album);
/// }
/// </code>
///
/// The executable behavior assertions (the degree-sign dual variant, accent folding, etc.) live in
/// Common's own <c>SearchQuerySanitizerTests</c>; this base pins only the cross-plugin invariants so
/// adding a plugin can never silently regress the contract.
///
/// <para>The <see cref="BuildPlanViaPlugin"/> hook is what catches a plugin whose real search path
/// diverges from <see cref="SearchQuerySanitizer.BuildPlan(string, string, SanitizerOptions)"/> — a
/// plugin must wire its ACTUAL request-generator tier construction here, not just re-call BuildPlan.</para>
/// </summary>
public abstract class SearchQuerySanitizerParityTestBase
{
    /// <summary>A reference artist used by the mandatory plan-shape assertions.</summary>
    protected const string ReferenceArtist = "Reference Artist";

    /// <summary>A reference album used when pairing an artist-kind corpus case into a plan.</summary>
    protected const string ReferenceAlbum = "Reference Album";

    /// <summary>The plugin's entrypoint into the canonical sanitizer (delegate to <see cref="SearchQuerySanitizer.Sanitize"/>).</summary>
    protected abstract SanitizedQuery SanitizeViaPlugin(string? raw);

    /// <summary>
    /// The plugin's REAL plan-construction path (the request generator's tier assembly), so this suite
    /// pins the path the live host actually drives — not merely a second call to
    /// <see cref="SearchQuerySanitizer.BuildPlan(string, string, SanitizerOptions)"/>.
    /// </summary>
    protected abstract SearchPlan BuildPlanViaPlugin(string artist, string album);

    /// <summary>The plugin's transport-encoder for a single variant (defaults to the canonical one).</summary>
    protected virtual string ToQueryParameterValue(string variant) =>
        SearchQuerySanitizer.ToQueryParameterValue(variant);

    public static IEnumerable<object[]> Corpus => SearchQueryCorpus.AllCases;

    public static IEnumerable<object[]> CasesWithExpectedVariant =>
        SearchQueryCorpus.All.Where(c => c.ExpectedVariantPresent != null).Select(c => new object[] { c });

    public static IEnumerable<object[]> CasesWithExpectedTierShape =>
        SearchQueryCorpus.All.Where(c => c.ExpectedTierShape != null).Select(c => new object[] { c });

    private static bool IsWithinBudget(string original)
    {
        var defaults = SanitizerOptions.Default;
        return original.Length <= defaults.MaxLength
            && original.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= defaults.MaxTokens;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void NeverThrows_AndReturnsNonNull(SearchQueryCase c)
    {
        var ex = Record.Exception(() => SanitizeViaPlugin(c.Raw));
        Assert.Null(ex);

        var result = SanitizeViaPlugin(c.Raw);
        Assert.NotNull(result.Original);
        Assert.NotNull(result.Variants);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void SignalAndVariantInvariants_Hold(SearchQueryCase c)
    {
        var result = SanitizeViaPlugin(c.Raw);

        if (result.HasSignal)
        {
            Assert.NotEmpty(result.Variants);
            Assert.All(result.Variants, v => Assert.False(string.IsNullOrWhiteSpace(v)));
            Assert.False(result.NeedsAlias, $"HasSignal but NeedsAlias for '{c.Raw}'");

            // Distinct (case-insensitive) and best-first (Original leads, unless the Original is
            // over the length/token budget — then Variants[0] is the budgeted prefix by design).
            Assert.Equal(result.Variants.Count, result.Variants.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            if (IsWithinBudget(result.Original))
            {
                Assert.Equal(result.Original, result.Variants[0]);
            }
        }
        else
        {
            Assert.Empty(result.Variants);
            Assert.True(result.NeedsAlias, $"No signal but NeedsAlias is false for '{c.Raw}'");
            Assert.Equal(string.Empty, result.Original);
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void OriginalIsIdempotent(SearchQueryCase c)
    {
        var once = SanitizeViaPlugin(c.Raw).Original;
        var twice = SanitizeViaPlugin(once).Original;
        Assert.Equal(once, twice);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void ToQueryParameterValue_NeverThrows_AndRoundTrips(SearchQueryCase c)
    {
        var result = SanitizeViaPlugin(c.Raw);
        foreach (var variant in result.Variants)
        {
            string encoded = null!;
            var ex = Record.Exception(() => encoded = ToQueryParameterValue(variant));
            Assert.Null(ex);
            Assert.Equal(variant, Uri.UnescapeDataString(encoded));
        }
    }

    /// <summary>
    /// A corpus case carrying <see cref="SearchQueryCase.ExpectedVariantPresent"/> must produce that exact
    /// variant (case-insensitive) through the plugin's sanitizer — the executable form of the corpus's
    /// human-readable <c>expectedHandling</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(CasesWithExpectedVariant))]
    public void ExpectedVariant_IsPresent(SearchQueryCase c)
    {
        var variants = SanitizeViaPlugin(c.Raw).Variants;
        Assert.Contains(
            variants,
            v => string.Equals(v, c.ExpectedVariantPresent, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A corpus case carrying <see cref="SearchQueryCase.ExpectedTierShape"/> must produce the declared
    /// plan shape through the plugin's REAL <see cref="BuildPlanViaPlugin"/> path.
    /// </summary>
    [Theory]
    [MemberData(nameof(CasesWithExpectedTierShape))]
    public void ExpectedTierShape_Holds(SearchQueryCase c)
    {
        var fieldOriginal = SanitizeViaPlugin(c.Raw).Original;
        var isArtist = string.Equals(c.Kind, "artist", StringComparison.OrdinalIgnoreCase);
        var plan = isArtist
            ? BuildPlanViaPlugin(c.Raw, ReferenceAlbum)
            : BuildPlanViaPlugin(ReferenceArtist, c.Raw);

        switch (c.ExpectedTierShape)
        {
            case "combined-plus-field-fallback":
                Assert.NotEmpty(plan.Tiers);
                Assert.Contains(plan.Tiers[0], v => v.Contains(fieldOriginal, StringComparison.OrdinalIgnoreCase));
                Assert.Contains(
                    plan.Tiers.Skip(1),
                    tier => tier.Any(v => string.Equals(v, fieldOriginal, StringComparison.OrdinalIgnoreCase)));
                break;

            case "artist-scoped":
                Assert.True(
                    SanitizeViaPlugin(c.Raw).RequiresArtistScope,
                    $"'{c.Raw}' declared artist-scoped but RequiresArtistScope is false");
                Assert.DoesNotContain(
                    plan.Tiers.Skip(1),
                    tier => tier.Any(v => string.Equals(v, fieldOriginal, StringComparison.OrdinalIgnoreCase)));
                break;

            default:
                throw new Xunit.Sdk.XunitException(
                    $"Unknown ExpectedTierShape '{c.ExpectedTierShape}' for '{c.Raw}'");
        }
    }

    /// <summary>
    /// Mandatory plan-shape invariants the plugin's REAL path must hold for an ordinary
    /// both-fields-have-signal search: a non-empty combined tier 0 that carries BOTH the artist and album
    /// tokens, plus a present, FULL (non-truncated) artist-only fallback tier.
    /// </summary>
    [Fact]
    public void BuildPlan_ShapeInvariants_Hold()
    {
        var plan = BuildPlanViaPlugin("Daft Punk", "Discovery");

        Assert.NotEmpty(plan.Tiers);
        Assert.NotEmpty(plan.Tiers[0]);

        // Combined tier carries both the artist and album signal.
        Assert.Contains(
            plan.Tiers[0],
            v => v.Contains("Daft Punk", StringComparison.OrdinalIgnoreCase)
                 && v.Contains("Discovery", StringComparison.OrdinalIgnoreCase));

        // A full (non-truncated) artist-only fallback tier is present.
        Assert.Contains(
            plan.Tiers.Skip(1),
            tier => tier.Any(v => string.Equals(v, "Daft Punk", StringComparison.OrdinalIgnoreCase)));
    }
}
