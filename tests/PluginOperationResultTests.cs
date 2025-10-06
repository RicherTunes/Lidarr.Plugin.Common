using System;
using Lidarr.Plugin.Abstractions.Results;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class PluginOperationResultTests
    {
        [Fact]
        public void Success_Result_ReportsAsSuccessful()
        {
            var result = PluginOperationResult<string>.Success("ok");

            Assert.True(result.IsSuccess);
            Assert.Equal("ok", result.GetValueOrThrow());
            Assert.Null(result.Error);
        }

        [Fact]
        public void Failure_Result_ExposesError()
        {
            var error = new PluginError(PluginErrorCode.RateLimited, "429 too many requests");
            var result = PluginOperationResult<string>.Failure(error);

            Assert.False(result.IsSuccess);
            Assert.Equal(error, result.Error);
            Assert.Throws<PluginOperationException>(() => result.GetValueOrThrow());
        }

        [Fact]
        public void NonGenericFailure_ThrowsOnEnsure()
        {
            var error = new PluginError(PluginErrorCode.AuthenticationExpired, "token expired");
            var result = PluginOperationResult.Failure(error);

            var exception = Assert.Throws<PluginOperationException>(result.EnsureSuccess);
            Assert.Equal(error, exception.Error);
        }
    }
}
