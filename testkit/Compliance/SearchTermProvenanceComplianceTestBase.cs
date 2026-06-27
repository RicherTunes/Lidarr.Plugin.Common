using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Intelligence;
using Xunit;

namespace Lidarr.Plugin.Common.TestKit.Compliance;

/// <summary>
/// Behavioral compliance axis: proves a plugin's indexer only ever sends the API search terms that were
/// produced by <see cref="SearchQuerySanitizer.BuildPlan(string, string, SanitizerOptions)"/> — closing
/// the loop the <see cref="SearchQuerySanitizerParityTestBase"/> opens (which pins the plan SHAPE but not
/// what the live FetchReleases actually issues).
///
/// <para>A plugin adopts it (Phase B) by capturing every query its real indexer hands to the transport
/// (e.g. via a fake HTTP handler or by intercepting the per-variant delegate) and returning the RAW
/// (decoded, not percent-encoded) query strings in issue order:</para>
///
/// <code>
/// public sealed class TidalSearchTermProvenanceTests : SearchTermProvenanceComplianceTestBase
/// {
///     protected override SearchPlan BuildPlanViaPlugin(string artist, string album) =>
///         TidalLidarrRequestGenerator.BuildPlanForTest(artist, album);
///     protected override Task&lt;IReadOnlyList&lt;string&gt;&gt; CaptureIssuedQueriesAsync(string artist, string album) =>
///         /* drive FetchReleases against a capturing transport, return the decoded queries */;
/// }
/// </code>
/// </summary>
public abstract class SearchTermProvenanceComplianceTestBase
{
    /// <summary>The artist used to drive the captured search (override to suit the plugin's fixtures).</summary>
    protected virtual string SampleArtist => "Daft Punk";

    /// <summary>The album used to drive the captured search.</summary>
    protected virtual string SampleAlbum => "Discovery";

    /// <summary>The plugin's REAL plan-construction path (the request generator's tier assembly).</summary>
    protected abstract SearchPlan BuildPlanViaPlugin(string artist, string album);

    /// <summary>
    /// Drive the plugin's REAL indexer search for (artist, album) against a captured transport and return
    /// every RAW (decoded, not percent-encoded) query string the indexer issued to the API, in issue order.
    /// </summary>
    protected abstract Task<IReadOnlyList<string>> CaptureIssuedQueriesAsync(string artist, string album);

    /// <summary>
    /// Record-and-assert: every query the indexer actually issued must be one of the variants
    /// <see cref="BuildPlanViaPlugin"/> produced — nothing reaches the API except via the plan.
    /// </summary>
    [Fact]
    public async Task EveryIssuedQuery_WasProducedViaBuildPlan()
    {
        var issued = await CaptureIssuedQueriesAsync(SampleArtist, SampleAlbum);
        Assert.NotEmpty(issued);

        var plan = BuildPlanViaPlugin(SampleArtist, SampleAlbum);
        var planned = new HashSet<string>(plan.Tiers.SelectMany(t => t), StringComparer.OrdinalIgnoreCase);

        Assert.All(issued, q => Assert.True(
            planned.Contains(q),
            $"indexer issued '{q}' which is NOT a BuildPlan variant — search terms must come only from BuildPlan"));
    }

    /// <summary>
    /// The first query the indexer issues must be the combined tier's first variant
    /// (<c>BuildPlan(artist, album).Tiers[0][0]</c>) — the most-specific query leads.
    /// </summary>
    [Fact]
    public async Task FirstIssuedQuery_EqualsCombinedTierFirstVariant()
    {
        var issued = await CaptureIssuedQueriesAsync(SampleArtist, SampleAlbum);
        Assert.NotEmpty(issued);

        var plan = BuildPlanViaPlugin(SampleArtist, SampleAlbum);
        Assert.NotEmpty(plan.Tiers);
        Assert.NotEmpty(plan.Tiers[0]);

        Assert.Equal(plan.Tiers[0][0], issued[0], StringComparer.OrdinalIgnoreCase);
    }
}
