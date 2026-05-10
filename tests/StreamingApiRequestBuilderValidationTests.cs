using System;
using Lidarr.Plugin.Common.Services.Http;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Validation tests for StreamingApiRequestBuilder - covers ArgumentNullException paths.
    /// Complements StreamingApiRequestBuilderCovTests.cs for full coverage.
    /// </summary>
    [Trait("Category", "Unit")]
    public class StreamingApiRequestBuilderValidationTests
    {
        #region Constructor Validation - Line 30

        [Fact]
        public void Constructor_NullBaseUrl_ThrowsArgumentNullException()
        {
            // Line 30: throw new ArgumentNullException(nameof(baseUrl))
            var exception = Assert.Throws<ArgumentNullException>(() => new StreamingApiRequestBuilder(null!));
            Assert.Equal("baseUrl", exception.ParamName);
        }

        #endregion

        #region Method() Validation - Line 47

        [Fact]
        public void Method_NullMethod_ThrowsArgumentNullException()
        {
            // Line 47: throw new ArgumentNullException(nameof(method))
            var builder = new StreamingApiRequestBuilder("https://api.example.test");
            var exception = Assert.Throws<ArgumentNullException>(() => builder.Method(null!));
            Assert.Equal("method", exception.ParamName);
        }

        #endregion

        #region WithPolicy Validation - Line 213

        [Fact]
        public void WithPolicy_NullPolicy_ThrowsArgumentNullException()
        {
            // Line 213: throw new ArgumentNullException(nameof(policy))
            var builder = new StreamingApiRequestBuilder("https://api.example.test");
            var exception = Assert.Throws<ArgumentNullException>(() => builder.WithPolicy(null!));
            Assert.Equal("policy", exception.ParamName);
        }

        #endregion
    }
}
