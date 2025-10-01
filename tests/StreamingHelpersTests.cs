using System.Collections.Generic;
using Lidarr.Plugin.Common.Base;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class StreamingHelpersTests
    {
        [Fact]
        public void GenerateSearchCacheKey_IgnoresSensitiveParameters()
        {
            var withSensitive = StreamingIndexerHelpers.GenerateSearchCacheKey("svc", "query", new Dictionary<string, string>
            {
                ["token"] = "12345",
                ["plain"] = "value"
            });

            var withoutSensitive = StreamingIndexerHelpers.GenerateSearchCacheKey("svc", "query", new Dictionary<string, string>
            {
                ["plain"] = "value"
            });

            Assert.Equal(withoutSensitive, withSensitive);
        }

        [Fact]
        public void CreateMaskedSettings_MasksSensitiveProperties()
        {
            var settings = new SampleSettings
            {
                ApiKey = "abc",
                Plain = "value"
            };

            var masked = StreamingConfigHelpers.CreateMaskedSettings(settings);
            var dict = Assert.IsType<Dictionary<string, object>>(masked);

            Assert.Equal("[MASKED]", dict[nameof(SampleSettings.ApiKey)]);
            Assert.Equal("value", dict[nameof(SampleSettings.Plain)]);
        }

        private sealed class SampleSettings
        {
            public string ApiKey { get; set; } = string.Empty;
            public string Plain { get; set; } = string.Empty;
        }
    }
}
