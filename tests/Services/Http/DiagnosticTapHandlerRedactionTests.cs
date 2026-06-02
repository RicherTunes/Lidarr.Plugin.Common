using System.Reflection;
using Lidarr.Plugin.Common.Services.Http;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Http
{
    /// <summary>
    /// Regression (harden campaign): the diagnostic tap's redaction missed Set-Cookie and
    /// token/api-key headers, and signature/auth/session query params — leaking credentials into
    /// diagnostic logs. These guard the broadened predicates.
    /// </summary>
    public class DiagnosticTapHandlerRedactionTests
    {
        private static bool IsSensitiveHeader(string name)
            => (bool)typeof(DiagnosticTapHandler)
                .GetMethod("IsSensitiveHeader", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { name })!;

        private static bool IsSensitiveKey(string key)
            => (bool)typeof(DiagnosticTapHandler)
                .GetMethod("IsSensitiveKey", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { key })!;

        [Theory]
        [InlineData("Authorization", true)]
        [InlineData("Set-Cookie", true)]        // was missed (response credential)
        [InlineData("Music-User-Token", true)]  // was missed (ends with -Token)
        [InlineData("X-Api-Key", true)]         // was missed
        [InlineData("Cookie", true)]
        [InlineData("Content-Type", false)]
        [InlineData("Accept", false)]
        public void IsSensitiveHeader_CoversCredentialHeaders(string name, bool expected)
            => Assert.Equal(expected, IsSensitiveHeader(name));

        [Theory]
        [InlineData("request_sig", true)]       // was missed
        [InlineData("signature", true)]         // was missed
        [InlineData("user_auth_token", true)]
        [InlineData("sessionId", true)]         // was missed
        [InlineData("app_secret", true)]
        [InlineData("limit", false)]
        [InlineData("offset", false)]
        public void IsSensitiveKey_CoversCredentialParams(string key, bool expected)
            => Assert.Equal(expected, IsSensitiveKey(key));
    }
}
