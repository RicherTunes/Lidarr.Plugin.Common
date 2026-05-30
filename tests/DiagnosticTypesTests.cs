using Lidarr.Plugin.Common.Abstractions.Diagnostics;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Pins the canonical diagnostic-type identifiers. These strings are a stable contract surfaced
    /// in DiagnosticHealthResult + logs and are the union of the values qobuz/tidal/apple currently
    /// re-declare in their per-plugin *HealthDiagnostics classes. Plugins migrate to reference these,
    /// so a rename lands in one place; this test guards the values against accidental drift.
    /// </summary>
    public class DiagnosticTypesTests
    {
        [Fact]
        public void CanonicalDiagnosticTypeValues_AreStable()
        {
            Assert.Equal("auth_validate", DiagnosticTypes.AuthValidate);
            Assert.Equal("connectivity", DiagnosticTypes.Connectivity);
            Assert.Equal("stream_probe", DiagnosticTypes.StreamProbe);
            Assert.Equal("catalog_access", DiagnosticTypes.CatalogAccess);
        }
    }
}
