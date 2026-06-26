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
/// }
/// </code>
///
/// The executable behavior assertions (the degree-sign dual variant, accent folding, etc.) live in
/// Common's own <c>SearchQuerySanitizerTests</c>; this base pins only the cross-plugin invariants so
/// adding a plugin can never silently regress the contract.
/// </summary>
public abstract class SearchQuerySanitizerParityTestBase
{
    /// <summary>The plugin's entrypoint into the canonical sanitizer (delegate to <see cref="SearchQuerySanitizer.Sanitize"/>).</summary>
    protected abstract SanitizedQuery SanitizeViaPlugin(string? raw);

    /// <summary>The plugin's transport-encoder for a single variant (defaults to the canonical one).</summary>
    protected virtual string ToQueryParameterValue(string variant) =>
        SearchQuerySanitizer.ToQueryParameterValue(variant);

    public static IEnumerable<object[]> Corpus => SearchQueryCorpus.AllCases;

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

            // Distinct (case-insensitive) and best-first (Original leads).
            Assert.Equal(result.Variants.Count, result.Variants.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(result.Original, result.Variants[0]);
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
}
