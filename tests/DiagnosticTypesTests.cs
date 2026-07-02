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

        [Fact]
        public void CanonicalDiagnosticErrorCodeValues_AreStable()
        {
            Assert.Equal("AUTH_FAILED", DiagnosticErrorCodes.AuthFailed);
            Assert.Equal("CONNECTION_FAILED", DiagnosticErrorCodes.ConnectionFailed);
            Assert.Equal("RATE_LIMITED", DiagnosticErrorCodes.RateLimited);
            Assert.Equal("TIMEOUT", DiagnosticErrorCodes.Timeout);
            Assert.Equal("REGION_BLOCKED", DiagnosticErrorCodes.RegionBlocked);
            Assert.Equal("VALIDATION_FAILED", DiagnosticErrorCodes.ValidationFailed);
            Assert.Equal("NOT_FOUND", DiagnosticErrorCodes.NotFound);
            Assert.Equal("SERVER_ERROR", DiagnosticErrorCodes.ServerError);
            Assert.Equal("MODEL_NOT_FOUND", DiagnosticErrorCodes.ModelNotFound);
            Assert.Equal("PROVIDER_INIT_FAILED", DiagnosticErrorCodes.ProviderInitFailed);
        }
    }
}
