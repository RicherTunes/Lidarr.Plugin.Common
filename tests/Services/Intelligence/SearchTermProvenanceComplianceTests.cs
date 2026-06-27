using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Intelligence;
using Lidarr.Plugin.Common.TestKit.Compliance;

namespace Lidarr.Plugin.Common.Tests.Services.Intelligence;

/// <summary>
/// Common's own adoption of the behavioral provenance axis: a faithful "indexer" that issues exactly the
/// BuildPlan variants in order proves the shared base compiles and its assertions are correct. Plugins
/// wire their REAL FetchReleases capture in Phase B.
/// </summary>
public sealed class CommonSearchTermProvenanceComplianceTests : SearchTermProvenanceComplianceTestBase
{
    protected override SearchPlan BuildPlanViaPlugin(string artist, string album) =>
        SearchQuerySanitizer.BuildPlan(artist, album);

    protected override Task<IReadOnlyList<string>> CaptureIssuedQueriesAsync(string artist, string album)
    {
        var plan = SearchQuerySanitizer.BuildPlan(artist, album);
        IReadOnlyList<string> issued = plan.Tiers.SelectMany(t => t).ToList();
        return Task.FromResult(issued);
    }
}
