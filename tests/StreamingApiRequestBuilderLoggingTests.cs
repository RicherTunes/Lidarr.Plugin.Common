using System;
using Lidarr.Plugin.Common.Services.Http;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class StreamingApiRequestBuilderLoggingTests
    {
        [Fact]
        public void Should_Not_Include_Query_Values_In_Log_Url()
        {
            var builder = new StreamingApiRequestBuilder("https://example.test/api")
                .Endpoint("search")
                .Query("token", "abc")
                .Query("albumId", "123");

            var info = builder.BuildForLogging();

            Assert.Equal("https://example.test/api/search?albumId&token", info.Url);
            Assert.Equal("[redacted]", info.QueryParameters["token"]);
            Assert.Equal("123", info.QueryParameters["albumId"]);
            Assert.DoesNotContain("abc", info.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Should_Redact_Query_Values_Containing_UrlEncoded_Tokens()
        {
            var redirectUrl = Uri.EscapeDataString("https://example.test/callback?access_token=abc");

            var builder = new StreamingApiRequestBuilder("https://example.test/api")
                .Endpoint("auth")
                .Query("redirectUrl", redirectUrl);

            var info = builder.BuildForLogging();

            Assert.Equal("https://example.test/api/auth?redirectUrl", info.Url);
            Assert.Equal("[redacted]", info.QueryParameters["redirectUrl"]);
            Assert.DoesNotContain("access_token", info.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("abc", info.ToString(), StringComparison.Ordinal);
        }
    }
}

