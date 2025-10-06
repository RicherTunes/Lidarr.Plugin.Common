using System.Collections.Generic;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class MaskingRedactionTests
    {
        [Fact]
        public void MaskSensitiveParams_Redacts_Common_Secrets()
        {
            var input = new Dictionary<string, string>
            {
                {"Authorization", "Bearer abcdefghijkl"},
                {"X-Api-Key", "1234567890"},
                {"Refresh-Token", "r1r2r3"},
                {"Cookie", "session=abc"},
                {"q", "beatles"}
            };

            var masked = HttpClientExtensions.MaskSensitiveParams(input);
            Assert.DoesNotContain("abcdefghijkl", masked["Authorization"]);
            Assert.DoesNotContain("1234567890", masked["X-Api-Key"]);
            Assert.DoesNotContain("r1r2r3", masked["Refresh-Token"]);
            Assert.DoesNotContain("abc", masked["Cookie"]);
            Assert.Equal("beatles", masked["q"]);
        }
    }
}

