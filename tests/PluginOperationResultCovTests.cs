using System;
using System.Collections.Generic;
using Lidarr.Plugin.Abstractions.Results;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class PluginOperationResultCovTests
    {
        #region PluginOperationResult (non-generic) uncovered paths

        [Fact]
        public void Success_CreatesSuccessfulResult()
        {
            // Line 29: public static PluginOperationResult Success() => new(true, null);
            var result = PluginOperationResult.Success();

            Assert.True(result.IsSuccess);
            Assert.Null(result.Error);
        }

        [Fact]
        public void Failure_WithNullError_ThrowsArgumentNullException()
        {
            // Line 34: error ?? throw new ArgumentNullException(nameof(error))
            var exception = Assert.Throws<ArgumentNullException>(() => PluginOperationResult.Failure(null!));
            Assert.Equal("error", exception.ParamName);
        }

        [Fact]
        public void EnsureSuccess_OnSuccessResult_DoesNotThrow()
        {
            // Lines 39-45: EnsureSuccess when IsSuccess is true should not throw
            var result = PluginOperationResult.Success();

            var exception = Record.Exception(() => result.EnsureSuccess());
            Assert.Null(exception);
        }

        [Fact]
        public void Deconstruct_NonGeneric_ReturnsIsSuccessAndError()
        {
            // Lines 47-51: Deconstruct for non-generic result
            var error = new PluginError(PluginErrorCode.Unknown, "test error");
            var result = PluginOperationResult.Failure(error);

            var (isSuccess, deconstructedError) = result;

            Assert.False(isSuccess);
            Assert.Equal(error, deconstructedError);
        }

        [Fact]
        public void Deconstruct_NonGeneric_Success_ReturnsTrueAndNullError()
        {
            // Lines 47-51: Deconstruct for non-generic success result
            var result = PluginOperationResult.Success();

            var (isSuccess, deconstructedError) = result;

            Assert.True(isSuccess);
            Assert.Null(deconstructedError);
        }

        #endregion

        #region PluginOperationResult<T> uncovered paths

        [Fact]
        public void Generic_Failure_WithNullError_ThrowsArgumentNullException()
        {
            // Line 89: error ?? throw new ArgumentNullException(nameof(error))
            var exception = Assert.Throws<ArgumentNullException>(() => PluginOperationResult<string>.Failure(null!));
            Assert.Equal("error", exception.ParamName);
        }

        [Fact]
        public void Generic_GetValueOrDefault_OnSuccess_ReturnsValue()
        {
            // Line 107: GetValueOrDefault when IsSuccess is true
            var result = PluginOperationResult<int>.Success(42);

            var value = result.GetValueOrDefault(99);

            Assert.Equal(42, value);
        }

        [Fact]
        public void Generic_GetValueOrDefault_OnFailure_ReturnsFallback()
        {
            // Line 107: GetValueOrDefault when IsSuccess is false
            var error = new PluginError(PluginErrorCode.Unknown, "failed");
            var result = PluginOperationResult<int>.Failure(error);

            var value = result.GetValueOrDefault(99);

            Assert.Equal(99, value);
        }

        [Fact]
        public void Generic_GetValueOrDefault_NoFallback_OnFailure_ReturnsDefault()
        {
            // Line 107: GetValueOrDefault with default fallback
            var error = new PluginError(PluginErrorCode.Unknown, "failed");
            var result = PluginOperationResult<int>.Failure(error);

            var value = result.GetValueOrDefault();

            Assert.Equal(0, value);
        }

        [Fact]
        public void Generic_Deconstruct_ReturnsAllComponents()
        {
            // Lines 109-114: Deconstruct for generic result
            var result = PluginOperationResult<string>.Success("test value");

            var (isSuccess, value, error) = result;

            Assert.True(isSuccess);
            Assert.Equal("test value", value);
            Assert.Null(error);
        }

        [Fact]
        public void Generic_Deconstruct_Failure_ReturnsAllComponents()
        {
            // Lines 109-114: Deconstruct for generic failure result
            var expectedError = new PluginError(PluginErrorCode.RateLimited, "rate limited");
            var result = PluginOperationResult<string>.Failure(expectedError);

            var (isSuccess, value, error) = result;

            Assert.False(isSuccess);
            Assert.Null(value);
            Assert.Equal(expectedError, error);
        }

        #endregion

        #region PluginOperationException uncovered paths

        [Fact]
        public void PluginOperationException_Constructor_WithNullError_ThrowsArgumentNullException()
        {
            // Line 125: Error = error ?? throw new ArgumentNullException(nameof(error))
            var exception = Assert.Throws<ArgumentNullException>(() => new PluginOperationException(null!));
            Assert.Equal("error", exception.ParamName);
        }

        [Fact]
        public void PluginOperationException_Constructor_WithNullMessage_UsesDefaultMessage()
        {
            // Line 123: base(error?.Message ?? "Plugin operation failed.", error?.Exception)
            var error = new PluginError(PluginErrorCode.Unknown, null);
            var exception = new PluginOperationException(error);

            Assert.Equal("Plugin operation failed.", exception.Message);
        }

        [Fact]
        public void PluginOperationException_Constructor_WithInnerException_PreservesInnerException()
        {
            // Line 123: base(error?.Message ?? "Plugin operation failed.", error?.Exception)
            var innerException = new InvalidOperationException("inner");
            var error = new PluginError(PluginErrorCode.Unknown, "outer message", innerException);
            var exception = new PluginOperationException(error);

            Assert.Equal("outer message", exception.Message);
            Assert.Equal(innerException, exception.InnerException);
        }

        #endregion
    }
}
