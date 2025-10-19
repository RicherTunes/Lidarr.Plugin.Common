using System;
using Lidarr.Plugin.Common.Security.TokenProtection;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class AkvDataProtectionTests
    {
        [Fact]
        public void Wraps_With_AKV_When_Configured()
        {
#if NET8_0
            var akv = Environment.GetEnvironmentVariable("LP_COMMON_AKV_KEY_ID");
            var require = Environment.GetEnvironmentVariable("LP_COMMON_REQUIRE_AKV");
            if (string.IsNullOrWhiteSpace(akv))
            {
                // Not configured in this environment; do nothing.
                return;
            }
            Environment.SetEnvironmentVariable("LP_COMMON_PROTECTOR", "dataprotection");
            // Factory is not used here to avoid auto OS fallbacks
            var protector = DataProtectionTokenProtector.Create(
                applicationName: "Lidarr.Plugin.Common",
                keysDirectory: null,
                certificatePath: null,
                certificatePassword: null,
                certificateThumbprint: null,
                akvKeyIdentifier: akv);

            Assert.Equal("dataprotection-akv", protector.AlgorithmId);
            var plaintext = new byte[] {1,2,3,4,5};
            var cipher = protector.Protect(plaintext);
            var round = protector.Unprotect(cipher);
            Assert.Equal(plaintext, round);
#else
            // AKV wrapping is only validated on .NET 8+ in this package.
            return;
#endif
        }
    }
}

