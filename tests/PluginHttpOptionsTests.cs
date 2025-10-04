using System.Net.Http;
using Lidarr.Plugin.Common.Services.Http;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public sealed class PluginHttpOptionsTests
    {
        [Fact]
        public void OptionsKeys_CanCarryValues_OnRequest()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://example.test/search?q=a");

            req.Options.Set(PluginHttpOptions.EndpointKey, "/search");
            req.Options.Set(PluginHttpOptions.ProfileKey, "search");
            req.Options.Set(PluginHttpOptions.AuthScopeKey, "user:abc");

            Assert.True(req.Options.TryGetValue(PluginHttpOptions.EndpointKey, out var endpoint));
            Assert.Equal("/search", endpoint);
            Assert.True(req.Options.TryGetValue(PluginHttpOptions.ProfileKey, out var profile));
            Assert.Equal("search", profile);
            Assert.True(req.Options.TryGetValue(PluginHttpOptions.AuthScopeKey, out var scope));
            Assert.Equal("user:abc", scope);
        }

        [Fact]
        public void Builder_Sets_Standardized_OptionsKeys()
        {
            var builder = new StreamingApiRequestBuilder("https://api.test")
                .Endpoint("search")
                .Query("b", "2")
                .Query("a", "1")
                .WithPolicy(Lidarr.Plugin.Common.Utilities.ResiliencePolicy.Search);

            using var req = builder.Build();

            Assert.True(req.Options.TryGetValue(PluginHttpOptions.EndpointKey, out var endpoint));
            Assert.Equal("/search", endpoint);

            Assert.True(req.Options.TryGetValue(PluginHttpOptions.ProfileKey, out var profile));
            Assert.Equal("search", profile);

            Assert.True(req.Options.TryGetValue(PluginHttpOptions.ParametersKey, out var canonical));
            // parameters should be key-sorted: a first, then b
            Assert.Equal("a=1&b=2", canonical);
        }
    }
}
