using Lidarr.Plugin.Common.Security;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class SanitizeTests
    {
        [Theory]
        [InlineData("https://api.qobuz.com/track?id=123&token=secret", "api.qobuz.com")]
        [InlineData("http://example.com/path?user=bob", "example.com")]
        [InlineData("https://user:pass@example.com/path", "example.com")]
        [InlineData("invalid-url", "unknown")]
        [InlineData("", "unknown")]
        [InlineData(null, "unknown")]
        public void UrlHostOnly_Should_Extract_Host(string? input, string expected)
        {
            var result = Sanitize.UrlHostOnly(input);
            Assert.Equal(expected, result);
        }
    }
}

