using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.HostBridge;
using Lidarr.Plugin.Common.Services.Intelligence;
using Lidarr.Plugin.Common.TestKit.Compliance;

namespace Lidarr.Plugin.Common.Tests.Services.Intelligence;

/// <summary>
/// Common's own adoption of the behavioral provenance axis. Rather than hand-returning the plan strings
/// (a reconstruction that would make the base's assertions tautological), this exemplary "indexer"
/// round-trips every variant through the SAME <see cref="PlaceholderSearchUri"/> encode/decode seam the
/// host and <see cref="SearchPlanExecutor"/> use — the closest faithful stand-in for a real transport
/// capture. Plugins wire their REAL FetchReleases capture in Phase B; the stronger, harder-to-fake
/// <see cref="SearchRequestChainComplianceTestBase"/> drives the request generator directly.
/// </summary>
public sealed class CommonSearchTermProvenanceComplianceTests : SearchTermProvenanceComplianceTestBase
{
    private const string Scheme = "common-sample";

    protected override SearchPlan BuildPlanViaPlugin(string artist, string album) =>
        SearchQuerySanitizer.BuildPlan(artist, album);

    protected override Task<IReadOnlyList<string>> CaptureIssuedQueriesAsync(string artist, string album)
    {
        var plan = SearchQuerySanitizer.BuildPlan(artist, album);
        var issued = new List<string>();
        foreach (var variant in plan.Tiers.SelectMany(t => t))
        {
            // Encode as GetSearchRequests would, then decode as the executor does — exercise the seam.
            var url = PlaceholderSearchUri.Build(Scheme, variant);
            if (PlaceholderSearchUri.TryExtractQuery(url, Scheme, out var decoded))
            {
                issued.Add(decoded);
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(issued);
    }
}
