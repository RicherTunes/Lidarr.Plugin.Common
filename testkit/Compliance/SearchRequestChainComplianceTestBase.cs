using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.HostBridge;
using Lidarr.Plugin.Common.Services.Intelligence;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Behavioral compliance axis: proves a plugin's <c>GetSearchRequests</c> emits the COMPLETE, sanitized
/// request chain — every <see cref="SearchQuerySanitizer.BuildPlan(string, string, SanitizerOptions)"/>
/// variant, as a well-formed <see cref="PlaceholderSearchUri"/>, and nothing else.
///
/// <para>This closes the gap the weaker <see cref="SearchTermProvenanceComplianceTestBase"/> leaves open.
/// That base asks the plugin for the queries its transport <i>issued</i> at runtime (which a plugin can
/// satisfy by hand-reconstructing the plan, and which a stop-policy can legitimately truncate). This base
/// instead drives the REAL request-generator entry point and decodes its output through the same
/// <see cref="PlaceholderSearchUri"/> seam the host round-trips — so it cannot be faked with arbitrary
/// strings, and it is deterministic (the chain is built before any result-dependent stop-policy runs).</para>
///
/// <para>It catches two shipped finding-classes by default:</para>
/// <list type="bullet">
///   <item>a <c>Take(N)</c>/truncation that DROPS the artist-only fallback tier (the "Bleu Jeans Bleu /
///   Record n°V returned 0 results" bug);</item>
///   <item>a special-character query that reaches the API UN-sanitized (raw <c>°</c>, <c>©</c>, curly
///   quotes, ♯/♭) because the generator skipped <see cref="SearchQuerySanitizer"/>.</item>
/// </list>
///
/// <para>A plugin adopts it by driving its real generator and returning the placeholder URLs (the host
/// interaction stays in the plugin's test project, keeping this base host-free):</para>
/// <code>
/// public sealed class TidalSearchRequestChainTests : SearchRequestChainComplianceTestBase
/// {
///     protected override string PlaceholderScheme =&gt; "tidal";
///     protected override IReadOnlyList&lt;string&gt; GetSearchRequestUrls(string artist, string album)
///     {
///         var chain = new TidalLidarrRequestGenerator(...)
///             .GetSearchRequests(new AlbumSearchCriteria { ArtistQuery = artist, AlbumQuery = album });
///         return chain.GetAllTiers().SelectMany(t =&gt; t.Select(r =&gt; r.Url.FullUri)).ToList();
///     }
/// }
/// </code>
/// </summary>
public abstract class SearchRequestChainComplianceTestBase
{
    /// <summary>Artist used to drive the chain (both fields carry signal ⇒ a fallback tier exists).</summary>
    protected virtual string SampleArtist => "Daft Punk";

    /// <summary>Album used to drive the chain.</summary>
    protected virtual string SampleAlbum => "Discovery";

    /// <summary>A special-character artist whose sanitized plan is known-clean (canonical audit case).</summary>
    protected virtual string SpecialArtist => "Bleu Jeans Bleu";

    /// <summary>A special-character album (degree sign) — sanitizer drops "n°V" → "nV".</summary>
    protected virtual string SpecialAlbum => "Record n°V";

    /// <summary>Raw characters that must never survive sanitization onto an outbound query.</summary>
    private static readonly char[] ForbiddenRawChars =
    {
        '°', // ° degree
        '©', // © copyright
        '’', // ’ right single quote
        '“', // “ left double quote
        '”', // ” right double quote
        '♯', // ♯ sharp
        '♭', // ♭ flat
    };

    /// <summary>The plugin's <see cref="PlaceholderSearchUri"/> scheme (e.g. "tidal", "qobuz").</summary>
    protected abstract string PlaceholderScheme { get; }

    /// <summary>
    /// Drive the plugin's REAL <c>GetSearchRequests</c> for (artist, album) and return the placeholder
    /// search URLs it produced, in chain order. Do NOT hand-rebuild — call the actual request generator.
    /// </summary>
    protected abstract IReadOnlyList<string> GetSearchRequestUrls(string artist, string album);

    private IReadOnlyList<string> DecodeChainOrThrow(string artist, string album)
    {
        var urls = GetSearchRequestUrls(artist, album);
        Assert.True(
            urls is { Count: > 0 },
            "GetSearchRequests produced no requests — the chain must issue at least the combined query.");

        var queries = new List<string>(urls.Count);
        foreach (var url in urls)
        {
            Assert.True(
                PlaceholderSearchUri.TryExtractQuery(url, PlaceholderScheme, out var decoded),
                $"request URL '{url}' is not a well-formed '{PlaceholderScheme}://search?query=...' placeholder URI. " +
                "GetSearchRequests must build EVERY request via PlaceholderSearchUri.Build so the executor can decode it.");
            queries.Add(decoded);
        }

        return queries;
    }

    /// <summary>Every request the generator emits must be a decodable placeholder URI.</summary>
    [Fact]
    public void EveryRequest_IsAWellFormedPlaceholderUri()
    {
        _ = DecodeChainOrThrow(SampleArtist, SampleAlbum); // assertions live in the decoder
    }

    /// <summary>The first request must be the combined tier's most-specific variant.</summary>
    [Fact]
    public void FirstRequest_IsTheCombinedTierFirstVariant()
    {
        var chain = DecodeChainOrThrow(SampleArtist, SampleAlbum);
        var plan = SearchQuerySanitizer.BuildPlan(SampleArtist, SampleAlbum);
        Assert.NotEmpty(plan.Combined);
        Assert.Equal(plan.Combined[0], chain[0], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The chain must not issue any query that BuildPlan did not produce (no rogue terms).</summary>
    [Fact]
    public void Chain_ContainsOnlyBuildPlanVariants()
    {
        var chain = DecodeChainOrThrow(SampleArtist, SampleAlbum);
        var planned = new HashSet<string>(
            SearchQuerySanitizer.BuildPlan(SampleArtist, SampleAlbum).Tiers.SelectMany(t => t),
            StringComparer.OrdinalIgnoreCase);

        Assert.All(chain, q => Assert.True(
            planned.Contains(q),
            $"request chain issues '{q}', which is NOT a BuildPlan variant — search terms must come only from BuildPlan."));
    }

    /// <summary>
    /// The chain must issue EVERY BuildPlan variant, including the full artist-only fallback tier — a
    /// Take(N)/truncation that drops the fallback is the Bleu-Jeans/Record-n°V zero-results bug.
    /// </summary>
    [Fact]
    public void Chain_PreservesEveryBuildPlanVariant_IncludingArtistOnlyFallback()
    {
        var chain = new HashSet<string>(
            DecodeChainOrThrow(SampleArtist, SampleAlbum), StringComparer.OrdinalIgnoreCase);
        var plan = SearchQuerySanitizer.BuildPlan(SampleArtist, SampleAlbum);

        // Sanity: the sample is chosen so a real artist-only fallback tier exists to be dropped.
        Assert.True(plan.Tiers.Count > 1, "test sample must yield an artist-only fallback tier");

        var missing = plan.Tiers
            .SelectMany(t => t)
            .Where(v => !chain.Contains(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "request chain DROPS BuildPlan variant(s) — the full plan (including the artist-only fallback " +
            "tier) must be issued; a Take(N)/truncation that drops the fallback is the Bleu-Jeans/Record-n°V " +
            "zero-results bug. Missing: " + string.Join(", ", missing.Select(m => "'" + m + "'")));
    }

    /// <summary>
    /// A special-character search must run through the sanitizer before the placeholder URI is built:
    /// every chain query must be a sanitized BuildPlan variant, AND the chain must include at least one
    /// fully-cleaned variant (no raw <c>°</c>/<c>©</c>/curly-quote/accidental). The sanitizer deliberately
    /// also emits a raw-preserving variant (so an exact-match service can still hit), so we do NOT require
    /// every variant to be clean — we require the clean fallback to be PRESENT (it is what made
    /// "Record n°V" stop returning zero), and nothing outside the plan to be issued.
    /// </summary>
    [Fact]
    public void SpecialCharInput_IsSanitizedThroughoutTheChain()
    {
        var chain = DecodeChainOrThrow(SpecialArtist, SpecialAlbum);
        var planned = new HashSet<string>(
            SearchQuerySanitizer.BuildPlan(SpecialArtist, SpecialAlbum).Tiers.SelectMany(t => t),
            StringComparer.OrdinalIgnoreCase);

        // (a) the real sanitizer ran — no query outside the sanitized plan reaches the API.
        Assert.All(chain, q => Assert.True(
            planned.Contains(q),
            $"special-char chain issues '{q}', which is not a BuildPlan variant — the real sanitizer must run on the chain."));

        // (b) a fully-cleaned fallback variant exists — the query that makes special-char searches succeed.
        Assert.True(
            chain.Any(q => !q.Any(c => ForbiddenRawChars.Contains(c))),
            "the special-character chain has no fully-sanitized variant (every query still carries a raw " +
            $"{string.Join("/", ForbiddenRawChars)}). GetSearchRequests must run SearchQuerySanitizer so a clean " +
            "fallback query (e.g. 'Record nV') is issued — its absence is the Record-n°V zero-results bug.");
    }
}
