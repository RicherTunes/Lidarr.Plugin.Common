using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.HostBridge;
using Lidarr.Plugin.Common.Services.Intelligence;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Behavioral compliance axis for capped-chain search plugins: proves the plugin's search plan
/// produced via <see cref="CappedSearchChain.Build"/> obeys two invariants -- (a) over-specific
/// queries are bounded by <see cref="MaxOverSpecificQueries"/>, and (b) the artist-only catalogue
/// fallback is always present in the final chain, never truncated by the cap.
///
/// <para>Capped-chain plugins intentionally emit a SUBSET of the full
/// <see cref="SearchQuerySanitizer.BuildPlan"/> variants (to keep API calls bounded), so they
/// cannot use <see cref="SearchRequestChainComplianceTestBase"/>, which requires every plan
/// variant to be emitted. This base covers only the invariants that survive the cap: the cap
/// itself and the always-present artist-only fallback.</para>
///
/// <para>Adopt by subclassing from the plugin's test project:</para>
/// <code>
/// public sealed class QobuzCappedSearchChainTests : CappedSearchChainComplianceTestBase
/// {
///     protected override string PlaceholderScheme => "qobuz";
///     protected override int MaxOverSpecificQueries => QobuzRequestGenerator.MaxOverSpecificQueries;
///     protected override string GetExpectedArtistOnlyFallbackQuery(string artist, string album) => artist;
///     protected override IReadOnlyList&lt;string&gt; GetSearchRequestUrls(string artist, string album)
///     {
///         var gen = new QobuzRequestGenerator(...);
///         return gen.GetSearchRequests(new AlbumSearchCriteria { ArtistQuery = artist, AlbumQuery = album })
///             .GetAllTiers().SelectMany(t => t.Select(r => r.Url.FullUri)).ToList();
///     }
/// }
/// </code>
/// </summary>
public abstract class CappedSearchChainComplianceTestBase
{
    /// <summary>Artist used to drive the chain.</summary>
    protected virtual string SampleArtist => "Daft Punk";

    /// <summary>Album used to drive the chain.</summary>
    protected virtual string SampleAlbum => "Discovery";

    /// <summary>The plugin's <see cref="PlaceholderSearchUri"/> scheme (e.g. "qobuz").</summary>
    protected abstract string PlaceholderScheme { get; }

    /// <summary>
    /// The configured cap -- the <c>maxOverSpecific</c> value passed to
    /// <see cref="CappedSearchChain.Build"/> by this plugin's generator.
    /// </summary>
    protected abstract int MaxOverSpecificQueries { get; }

    /// <summary>
    /// Drive the plugin's host-free plan entry point for (artist, album) and return the placeholder
    /// search URLs it produced, in chain order.
    /// </summary>
    protected abstract IReadOnlyList<string> GetSearchRequestUrls(string artist, string album);

    /// <summary>
    /// The expected artist-only fallback query string for the given (artist, album) input -- the
    /// value the plugin passes as <c>artistOnlyFallback</c> to <see cref="CappedSearchChain.Build"/>.
    /// Typically the sanitized artist name alone.
    /// </summary>
    protected abstract string GetExpectedArtistOnlyFallbackQuery(string artist, string album);

    private IReadOnlyList<string> DecodeChainOrThrow(string artist, string album)
    {
        var urls = GetSearchRequestUrls(artist, album);
        Assert.True(
            urls is { Count: > 0 },
            "GetSearchRequestUrls produced no requests -- the capped chain must issue at least the artist-only fallback.");

        var queries = new List<string>(urls.Count);
        foreach (var url in urls)
        {
            Assert.True(
                PlaceholderSearchUri.TryExtractQuery(url, PlaceholderScheme, out var decoded),
                $"request URL '{url}' is not a well-formed '{PlaceholderScheme}://search?query=...' placeholder URI.");
            queries.Add(decoded);
        }

        return queries;
    }

    /// <summary>
    /// The number of over-specific (non-fallback) queries in the chain must not exceed
    /// <see cref="MaxOverSpecificQueries"/> -- the cap configured in the plugin's generator.
    /// </summary>
    [Fact]
    public void OverSpecificQueryCount_DoesNotExceedCap()
    {
        var chain = DecodeChainOrThrow(SampleArtist, SampleAlbum);
        var fallback = GetExpectedArtistOnlyFallbackQuery(SampleArtist, SampleAlbum);

        var overSpecific = chain
            .Where(q => !string.Equals(q, fallback, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            overSpecific.Count <= MaxOverSpecificQueries,
            $"chain issues {overSpecific.Count} over-specific queries but cap is {MaxOverSpecificQueries}. " +
            $"Over-specific: {string.Join(", ", overSpecific.Select(q => "'" + q + "'"))}");
    }

    /// <summary>
    /// The artist-only fallback must always be present in the final chain -- the cap must never
    /// truncate it away. A missing fallback is the "Bleu Jeans Bleu - Record n°V" zero-results bug.
    /// </summary>
    [Fact]
    public void ArtistOnlyFallback_IsAlwaysPresent()
    {
        var chain = DecodeChainOrThrow(SampleArtist, SampleAlbum);
        var fallback = GetExpectedArtistOnlyFallbackQuery(SampleArtist, SampleAlbum);

        Assert.True(
            !string.IsNullOrWhiteSpace(fallback),
            "GetExpectedArtistOnlyFallbackQuery returned null/blank -- the hook must return the plugin's artist-only fallback query.");

        Assert.True(
            chain.Contains(fallback, StringComparer.OrdinalIgnoreCase),
            $"artist-only fallback '{fallback}' is NOT present in the capped chain. The cap must append the " +
            "artist-only fallback unconditionally -- truncating it is the Bleu-Jeans/Record-n+V zero-results bug. " +
            $"Chain: {string.Join(", ", chain.Select(q => "'" + q + "'"))}");
    }
}
