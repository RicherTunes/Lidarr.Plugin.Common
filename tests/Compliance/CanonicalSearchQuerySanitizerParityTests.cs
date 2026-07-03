using Lidarr.Plugin.Common.Services.Intelligence;
using Lidarr.Plugin.Common.TestKit.Compliance;

namespace Lidarr.Plugin.Common.Tests.Compliance;

/// <summary>
/// Self-coverage: runs the shared <see cref="SearchQuerySanitizerParityTestBase"/> corpus suite against
/// the CANONICAL sanitizer itself, so the executable corpus assertions (expectedVariantPresent / tier
/// shapes) are proven inside Common BEFORE any plugin re-pins. A plugin parity subclass that diverges
/// from this canonical behavior then fails loudly instead of silently shipping a regression.
/// </summary>
public sealed class CanonicalSearchQuerySanitizerParityTests : SearchQuerySanitizerParityTestBase
{
    protected override SanitizedQuery SanitizeViaPlugin(string? raw) => SearchQuerySanitizer.Sanitize(raw);

    protected override SearchPlan BuildPlanViaPlugin(string artist, string album) =>
        SearchQuerySanitizer.BuildPlan(artist, album);
}
