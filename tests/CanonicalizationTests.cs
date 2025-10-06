using System;
using System.Net.Http;
using Lidarr.Plugin.Common.Services.Http;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class CanonicalizationTests
    {
        [Fact]
        public void Builder_Canonicalizes_Multivalue_And_Preserves_Empty()
        {
            var builder = new StreamingApiRequestBuilder("https://canon.example")
                .Endpoint("search")
                .Query("a", "2")
                .Query("a", "1")
                .Query("b", "");

            using var req = builder.Build();
            Assert.True(req.Options.TryGetValue(PluginHttpOptions.ParametersKey, out string? canon));
            Assert.NotNull(canon);
            // multivalue joined with comma, then percent-encoded with lowercase hex
            Assert.Equal("a=1%2c2&b=", canon);
        }
    }
}
