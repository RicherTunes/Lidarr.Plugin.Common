using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Data;

/// <summary>
/// One tricky-character search-query case shared by Common and every plugin so they all
/// assert against the SAME data (parity-as-tests). The executable expectations live in the
/// test methods (the behavior contracts); <see cref="TrickyProperty"/> / <see cref="ExpectedHandling"/>
/// carry the human-readable invariant, and <see cref="Category"/> lets each behavior subscribe to its slice.
///
/// <para>The optional <see cref="ExpectedVariantPresent"/> / <see cref="ExpectedTierShape"/> fields make a
/// case's expectation EXECUTABLE: when non-null the shared parity base enforces them against the plugin's
/// sanitizer/plan path, and skips them when null.</para>
/// </summary>
public sealed record SearchQueryCase(
    string Raw,
    string Kind,
    string Category,
    string TrickyProperty,
    string ExpectedHandling,
    bool RealExample,
    string? ExpectedVariantPresent = null,
    string? ExpectedTierShape = null)
{
    // Drives readable xUnit test-explorer names ("category: raw").
    public override string ToString() => $"{Category}: {Raw}";
}

/// <summary>
/// Loads the shared search-query corpus from the embedded JSON resource (once, cached) and exposes
/// xUnit MemberData / TheoryData adapters. Mirrors the <see cref="EmbeddedJson"/> loader convention.
/// </summary>
public static class SearchQueryCorpus
{
    private const string ResourcePath = "Data/search-query-corpus.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<IReadOnlyList<SearchQueryCase>> Cases =
        new(LoadFromResource, isThreadSafe: true);

    /// <summary>All corpus cases, deserialized once from the embedded resource and cached.</summary>
    public static IReadOnlyList<SearchQueryCase> All => Cases.Value;

    /// <summary>The distinct set of category tags present in the corpus.</summary>
    public static IReadOnlyCollection<string> Categories =>
        All.Select(c => c.Category).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>All cases tagged with <paramref name="category"/>.</summary>
    public static IEnumerable<SearchQueryCase> ByCategory(string category) =>
        All.Where(c => string.Equals(c.Category, category, StringComparison.Ordinal));

    /// <summary>All cases of a given <paramref name="kind"/> (artist/album/track/...).</summary>
    public static IEnumerable<SearchQueryCase> ByKind(string kind) =>
        All.Where(c => string.Equals(c.Kind, kind, StringComparison.Ordinal));

    /// <summary>
    /// xUnit <c>[MemberData]</c> adapter exposing every case (wrapped in <c>object[]</c> so the record
    /// serializes through xUnit's data discovery).
    /// </summary>
    public static IEnumerable<object[]> AllCases => All.Select(c => new object[] { c });

    /// <summary>xUnit <c>[MemberData]</c> adapter for the union of the given <paramref name="categories"/>.</summary>
    public static IEnumerable<object[]> CasesIn(params string[] categories)
    {
        var wanted = new HashSet<string>(categories, StringComparer.Ordinal);
        return All.Where(c => wanted.Contains(c.Category)).Select(c => new object[] { c });
    }

    /// <summary>Strongly-typed <see cref="TheoryData{T}"/> for the union of the given <paramref name="categories"/>.</summary>
    public static TheoryData<SearchQueryCase> TheoryFor(params string[] categories)
    {
        var wanted = new HashSet<string>(categories, StringComparer.Ordinal);
        var data = new TheoryData<SearchQueryCase>();
        foreach (var c in All.Where(c => wanted.Contains(c.Category)))
        {
            data.Add(c);
        }

        return data;
    }

    private static IReadOnlyList<SearchQueryCase> LoadFromResource()
    {
        var json = EmbeddedJson.ReadAsString(ResourcePath);
        var cases = JsonSerializer.Deserialize<List<SearchQueryCase>>(json, SerializerOptions)
                    ?? throw new InvalidOperationException($"Search-query corpus '{ResourcePath}' deserialized to null.");
        return cases;
    }
}
