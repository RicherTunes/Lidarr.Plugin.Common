using Lidarr.Plugin.Common.Services.Http;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Http
{
    /// <summary>
    /// The diagnostic HTTP tap (LIDARR_PLUGIN_HTTP_TAP) logs a snippet of textual/JSON response
    /// bodies. OAuth/token endpoints return access_token / refresh_token / client_secret in the
    /// body, so the snippet must be redacted before it reaches the log — headers and query params
    /// already are.
    /// </summary>
    public class DiagnosticTapHandlerBodyRedactionTests
    {
        [Fact]
        public void RedactBodyForLog_masks_oauth_token_values_in_json()
        {
            var body = "{\"access_token\":\"AT_eyJsecretpayloadsig\",\"refresh_token\":\"RT_supersecret123\",\"expires_in\":3600}";

            var redacted = DiagnosticTapHandler.RedactBodyForLog(body);

            Assert.DoesNotContain("AT_eyJsecretpayloadsig", redacted);
            Assert.DoesNotContain("RT_supersecret123", redacted);
            Assert.Contains("REDACTED", redacted);
            Assert.Contains("expires_in", redacted); // non-secret key retained
        }

        [Fact]
        public void RedactBodyForLog_masks_secret_apikey_code_password_keeps_plain_fields()
        {
            var body = "{\"client_secret\":\"SEKRET_VALUE\",\"api_key\":\"APIKEY_VALUE\",\"code\":\"AUTHCODE_VALUE\",\"password\":\"PASSWORD_VALUE\",\"name\":\"Real Name\"}";

            var redacted = DiagnosticTapHandler.RedactBodyForLog(body);

            Assert.DoesNotContain("SEKRET_VALUE", redacted);
            Assert.DoesNotContain("APIKEY_VALUE", redacted);
            Assert.DoesNotContain("AUTHCODE_VALUE", redacted);
            Assert.DoesNotContain("PASSWORD_VALUE", redacted);
            Assert.Contains("\"name\":\"Real Name\"", redacted); // non-secret field preserved
        }

        [Fact]
        public void RedactBodyForLog_finds_nested_token()
        {
            var body = "{\"data\":{\"session\":{\"auth_token\":\"NESTED_SECRET\"}},\"ok\":true}";

            var redacted = DiagnosticTapHandler.RedactBodyForLog(body);

            Assert.DoesNotContain("NESTED_SECRET", redacted);
            Assert.Contains("REDACTED", redacted);
        }

        [Fact]
        public void RedactBodyForLog_passes_through_non_sensitive_and_null()
        {
            Assert.Null(DiagnosticTapHandler.RedactBodyForLog(null));
            Assert.Equal(string.Empty, DiagnosticTapHandler.RedactBodyForLog(string.Empty));

            var plain = "{\"items\":[1,2,3],\"status\":\"ok\"}";
            Assert.Equal(plain, DiagnosticTapHandler.RedactBodyForLog(plain));
        }
    }
}
