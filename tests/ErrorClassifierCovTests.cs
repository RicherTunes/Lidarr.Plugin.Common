using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Errors;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Coverage tests for LlmErrorMapper - the error classification/mapping logic.
    /// Target: src/Errors/LlmErrorMapper.cs (Note: ErrorClassifier.cs does not exist)
    /// </summary>
    public class ErrorClassifierCovTests
    {
        #region MapHttpError - Authentication Errors (401/403)

        [Fact]
        public void MapHttpError_401_ReturnsAuthenticationException()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 401);

            // Assert - Line 30 of LlmErrorMapper.cs: 401 => new AuthenticationException
            var authEx = Assert.IsType<AuthenticationException>(result);
            Assert.Equal("test-provider", authEx.ProviderId);
            Assert.Equal(LlmErrorCode.AuthenticationFailed, authEx.ErrorCode);
            Assert.Equal("Invalid API key or credentials", authEx.Message);
            Assert.False(authEx.IsRetryable);
        }

        [Fact]
        public void MapHttpError_401_WithInnerException_ReturnsAuthenticationExceptionWithInner()
        {
            // Arrange
            var inner = new Exception("Inner error");

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 401, null, inner);

            // Assert
            var authEx = Assert.IsType<AuthenticationException>(result);
            Assert.Same(inner, authEx.InnerException);
        }

        [Fact]
        public void MapHttpError_403_ReturnsAuthenticationExceptionWithAuthorizationFailed()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 403);

            // Assert - Line 31 of LlmErrorMapper.cs: 403 => new AuthenticationException with AuthorizationFailed
            var authEx = Assert.IsType<AuthenticationException>(result);
            Assert.Equal(LlmErrorCode.AuthorizationFailed, authEx.ErrorCode);
            Assert.Equal("Access denied - check API key permissions", authEx.Message);
            Assert.False(authEx.IsRetryable);
        }

        #endregion

        #region MapHttpError - Rate Limiting (429)

        [Fact]
        public void MapHttpError_429_ReturnsRateLimitException()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 429);

            // Assert - Line 32 of LlmErrorMapper.cs: 429 => new RateLimitException
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Equal(LlmErrorCode.RateLimited, rateLimitEx.ErrorCode);
            Assert.Equal("Rate limit exceeded", rateLimitEx.Message);
            Assert.True(rateLimitEx.IsRetryable);
            Assert.Null(rateLimitEx.RetryAfter);
        }

        [Fact]
        public void MapHttpError_429_WithRetryAfterJson_ParsesRetryAfter()
        {
            // Arrange - Line 76-79 of LlmErrorMapper.cs: ParseRetryAfter regex
            var responseBody = @"{""retry_after"": 60}";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 429, responseBody);

            // Assert
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Equal(TimeSpan.FromSeconds(60), rateLimitEx.RetryAfter);
        }

        [Fact]
        public void MapHttpError_429_WithRetryAfterHyphenatedJson_ParsesRetryAfter()
        {
            // Arrange - Line 76: regex matches retry-after or retry_after
            var responseBody = @"{""retry-after"": 120}";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 429, responseBody);

            // Assert
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Equal(TimeSpan.FromSeconds(120), rateLimitEx.RetryAfter);
        }

        [Fact]
        public void MapHttpError_429_WithRetryAfterEquals_ParsesRetryAfter()
        {
            // Arrange - Line 78: regex matches colon or equals
            var responseBody = @"retry_after = 30";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 429, responseBody);

            // Assert
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Equal(TimeSpan.FromSeconds(30), rateLimitEx.RetryAfter);
        }

        [Fact]
        public void MapHttpError_429_WithDecimalRetryAfter_ParsesRetryAfter()
        {
            // Arrange - Line 78: regex matches (\d+(?:\.\d+)?)
            var responseBody = @"retry_after: 1.5";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 429, responseBody);

            // Assert
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Equal(TimeSpan.FromSeconds(1.5), rateLimitEx.RetryAfter);
        }

        [Fact]
        public void MapHttpError_429_WithInvalidRetryAfter_ReturnsNullRetryAfter()
        {
            // Arrange
            var responseBody = @"{""retry_after"": ""invalid""}";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 429, responseBody);

            // Assert
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Null(rateLimitEx.RetryAfter);
        }

        [Fact]
        public void MapHttpError_429_WithNullResponseBody_ReturnsNullRetryAfter()
        {
            // Arrange - Line 71-72 of LlmErrorMapper.cs: null/empty check
            string? responseBody = null;

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 429, responseBody);

            // Assert
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Null(rateLimitEx.RetryAfter);
        }

        #endregion

        #region MapHttpError - Client Errors (400, 404)

        [Fact]
        public void MapHttpError_400_ReturnsProviderExceptionWithInvalidRequest()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 400);

            // Assert - Line 33 of LlmErrorMapper.cs: 400 => ProviderException with InvalidRequest
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.InvalidRequest, providerEx.ErrorCode);
            Assert.Equal("Invalid request", providerEx.Message);
        }

        [Fact]
        public void MapHttpError_400_WithJsonMessage_ParsesErrorMessage()
        {
            // Arrange - Line 98-101 of LlmErrorMapper.cs: ParseErrorMessage regex
            var responseBody = @"{""message"": ""Invalid parameter value""}";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 400, responseBody);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal("Invalid parameter value", providerEx.Message);
        }

        [Fact]
        public void MapHttpError_400_WithErrorField_ParsesErrorMessage()
        {
            // Arrange - Line 100: regex matches "message" or "error"
            var responseBody = @"{""error"": ""Bad request""}";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 400, responseBody);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal("Bad request", providerEx.Message);
        }

        [Fact]
        public void MapHttpError_400_WithNullMessageField_ReturnsDefaultMessage()
        {
            // Arrange
            var responseBody = @"{""foo"": ""bar""}";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 400, responseBody);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal("Invalid request", providerEx.Message);
        }

        [Fact]
        public void MapHttpError_400_WithEmptyResponseBody_ReturnsDefaultMessage()
        {
            // Arrange - Line 94-95 of LlmErrorMapper.cs: null/empty check
            var responseBody = "";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 400, responseBody);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal("Invalid request", providerEx.Message);
        }

        [Fact]
        public void MapHttpError_404_ReturnsProviderExceptionWithModelNotFound()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 404);

            // Assert - Line 34 of LlmErrorMapper.cs: 404 => ModelNotFound
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.ModelNotFound, providerEx.ErrorCode);
            Assert.Equal("Model or endpoint not found", providerEx.Message);
            Assert.False(providerEx.IsRetryable);
        }

        #endregion

        #region MapHttpError - Server Errors (500, 502, 503, 504)

        [Fact]
        public void MapHttpError_500_ReturnsProviderExceptionWithProviderUnavailable()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 500);

            // Assert - Line 35 of LlmErrorMapper.cs: 500 => ProviderUnavailable
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.ProviderUnavailable, providerEx.ErrorCode);
            Assert.Equal("Internal server error", providerEx.Message);
            Assert.True(providerEx.IsRetryable); // Line 17 of ProviderException.cs
        }

        [Fact]
        public void MapHttpError_502_ReturnsProviderExceptionWithProviderUnavailable()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 502);

            // Assert - Line 36 of LlmErrorMapper.cs: 502 => ProviderUnavailable (Bad gateway)
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.ProviderUnavailable, providerEx.ErrorCode);
            Assert.Equal("Bad gateway", providerEx.Message);
            Assert.True(providerEx.IsRetryable);
        }

        [Fact]
        public void MapHttpError_503_ReturnsProviderExceptionWithProviderOverloaded()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 503);

            // Assert - Line 37 of LlmErrorMapper.cs: 503 => ProviderOverloaded
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.ProviderOverloaded, providerEx.ErrorCode);
            Assert.Equal("Service unavailable - provider may be overloaded", providerEx.Message);
            Assert.True(providerEx.IsRetryable); // Line 18 of ProviderException.cs
        }

        [Fact]
        public void MapHttpError_504_ReturnsNetworkExceptionWithTimeout()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 504);

            // Assert - Line 38 of LlmErrorMapper.cs: 504 => NetworkException with Timeout
            var networkEx = Assert.IsType<NetworkException>(result);
            Assert.Equal(LlmErrorCode.Timeout, networkEx.ErrorCode);
            Assert.Equal("Gateway timeout", networkEx.Message);
            Assert.True(networkEx.IsRetryable); // NetworkException is always retryable
        }

        #endregion

        #region MapHttpError - Unknown Status Codes

        [Fact]
        public void MapHttpError_UnknownStatusCode_ReturnsProviderExceptionWithUnknown()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 418);

            // Assert - Line 39 of LlmErrorMapper.cs: _ => ProviderException with Unknown
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.Unknown, providerEx.ErrorCode);
            Assert.Equal("Unexpected HTTP error: 418", providerEx.Message);
        }

        [Fact]
        public void MapHttpError_StatusCode0_ReturnsProviderExceptionWithUnknown()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 0);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.Unknown, providerEx.ErrorCode);
            Assert.Equal("Unexpected HTTP error: 0", providerEx.Message);
        }

        [Fact]
        public void MapHttpError_NegativeStatusCode_ReturnsProviderExceptionWithUnknown()
        {
            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", -1);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.Unknown, providerEx.ErrorCode);
            Assert.Equal("Unexpected HTTP error: -1", providerEx.Message);
        }

        #endregion

        #region MapException - TaskCanceledException

        [Fact]
        public void MapException_TaskCanceledWithCancellation_ReturnsNetworkExceptionCancelled()
        {
            // Arrange
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var tce = new TaskCanceledException("Task was cancelled", new Exception(), cts.Token);

            // Act
            var result = LlmErrorMapper.MapException("test-provider", tce);

            // Assert - Line 51-52 of LlmErrorMapper.cs: TaskCanceledException with IsCancellationRequested
            var networkEx = Assert.IsType<NetworkException>(result);
            Assert.Equal(LlmErrorCode.Timeout, networkEx.ErrorCode);
            Assert.Equal("Request was cancelled", networkEx.Message);
            Assert.Same(tce, networkEx.InnerException);
        }

        [Fact]
        public void MapException_TaskCanceledWithoutCancellation_ReturnsNetworkExceptionTimeout()
        {
            // Arrange - Line 53-54: TaskCanceledException without cancellation = timeout
            var tce = new TaskCanceledException("Task timed out");

            // Act
            var result = LlmErrorMapper.MapException("test-provider", tce);

            // Assert
            var networkEx = Assert.IsType<NetworkException>(result);
            Assert.Equal(LlmErrorCode.Timeout, networkEx.ErrorCode);
            Assert.Equal("Request timed out", networkEx.Message);
            Assert.Same(tce, networkEx.InnerException);
        }

        #endregion

        #region MapException - HttpRequestException

        [Fact]
        public void MapException_HttpRequestExceptionWithStatusCode_MapsToHttpError()
        {
            // Arrange - Line 55-56: HttpRequestException with StatusCode => MapHttpError
            var hre = new HttpRequestException("Bad request", null, HttpStatusCode.BadRequest);

            // Act
            var result = LlmErrorMapper.MapException("test-provider", hre);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.InvalidRequest, providerEx.ErrorCode);
            Assert.Same(hre, providerEx.InnerException);
        }

        [Fact]
        public void MapException_HttpRequestException401_MapsToAuthenticationException()
        {
            // Arrange
            var hre = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

            // Act
            var result = LlmErrorMapper.MapException("test-provider", hre);

            // Assert
            var authEx = Assert.IsType<AuthenticationException>(result);
            Assert.Equal(LlmErrorCode.AuthenticationFailed, authEx.ErrorCode);
        }

        [Fact]
        public void MapException_HttpRequestException429_MapsToRateLimitException()
        {
            // Arrange
            var hre = new HttpRequestException("Too many requests", null, (HttpStatusCode)429);

            // Act
            var result = LlmErrorMapper.MapException("test-provider", hre);

            // Assert
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Equal(LlmErrorCode.RateLimited, rateLimitEx.ErrorCode);
        }

        [Fact]
        public void MapException_HttpRequestExceptionWithoutStatusCode_ReturnsNetworkException()
        {
            // Arrange - Line 57-58: HttpRequestException without StatusCode => NetworkException
            var hre = new HttpRequestException("Connection failed");

            // Act
            var result = LlmErrorMapper.MapException("test-provider", hre);

            // Assert
            var networkEx = Assert.IsType<NetworkException>(result);
            Assert.Equal(LlmErrorCode.ConnectionFailed, networkEx.ErrorCode);
            Assert.Contains("Connection failed", networkEx.Message);
            Assert.Same(hre, networkEx.InnerException);
        }

        #endregion

        #region MapException - OperationCanceledException

        [Fact]
        public void MapException_OperationCanceledException_ReturnsNetworkExceptionTimeout()
        {
            // Arrange - Line 59-60: OperationCanceledException => NetworkException with Timeout
            var oce = new OperationCanceledException("Operation was cancelled");

            // Act
            var result = LlmErrorMapper.MapException("test-provider", oce);

            // Assert
            var networkEx = Assert.IsType<NetworkException>(result);
            Assert.Equal(LlmErrorCode.Timeout, networkEx.ErrorCode);
            Assert.Equal("Operation was cancelled", networkEx.Message);
            Assert.Same(oce, networkEx.InnerException);
        }

        #endregion

        #region MapException - Unknown Exceptions

        [Fact]
        public void MapException_GenericException_ReturnsProviderExceptionWithUnknown()
        {
            // Arrange - Line 61: _ => ProviderException with Unknown
            var ex = new InvalidOperationException("Something went wrong");

            // Act
            var result = LlmErrorMapper.MapException("test-provider", ex);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.Unknown, providerEx.ErrorCode);
            Assert.Equal("Unexpected error: Something went wrong", providerEx.Message);
            Assert.Same(ex, providerEx.InnerException);
        }

        [Fact]
        public void MapException_NullReferenceException_ReturnsProviderExceptionWithUnknown()
        {
            // Arrange
            var ex = new NullReferenceException("Object reference not set");

            // Act
            var result = LlmErrorMapper.MapException("test-provider", ex);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.Unknown, providerEx.ErrorCode);
            Assert.Contains("Object reference not set", providerEx.Message);
        }

        [Fact]
        public void MapException_ArgumentException_ReturnsProviderExceptionWithUnknown()
        {
            // Arrange
            var ex = new ArgumentException("Invalid argument");

            // Act
            var result = LlmErrorMapper.MapException("test-provider", ex);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal(LlmErrorCode.Unknown, providerEx.ErrorCode);
            Assert.Contains("Invalid argument", providerEx.Message);
        }

        #endregion

        #region ArgumentNullException - Null ProviderId

        [Fact]
        public void MapHttpError_NullProviderId_ThrowsArgumentNullException()
        {
            // Act & Assert - Line 32 of LlmProviderException.cs: providerId null check
            Assert.Throws<ArgumentNullException>(() =>
                LlmErrorMapper.MapHttpError(null!, 401));
        }

        [Fact]
        public void MapException_NullProviderId_ThrowsArgumentNullException()
        {
            // Arrange
            var ex = new Exception("Test");

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                LlmErrorMapper.MapException(null!, ex));
        }

        #endregion

        #region ProviderId Preservation

        [Fact]
        public void MapHttpError_PreservesProviderId()
        {
            // Arrange
            var providerId = "my-custom-provider";

            // Act
            var result = LlmErrorMapper.MapHttpError(providerId, 500);

            // Assert
            Assert.Equal(providerId, result.ProviderId);
        }

        [Fact]
        public void MapException_PreservesProviderId()
        {
            // Arrange
            var providerId = "another-provider";
            var ex = new Exception("Test");

            // Act
            var result = LlmErrorMapper.MapException(providerId, ex);

            // Assert
            Assert.Equal(providerId, result.ProviderId);
        }

        #endregion

        #region RetryAfter Parsing Edge Cases

        [Fact]
        public void MapHttpError_429_WithVeryLargeRetryAfter_ParsesCorrectly()
        {
            // Arrange
            var responseBody = @"retry_after: 86400"; // 1 day

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 429, responseBody);

            // Assert
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Equal(TimeSpan.FromDays(1), rateLimitEx.RetryAfter);
        }

        [Fact]
        public void MapHttpError_429_WithZeroRetryAfter_ParsesCorrectly()
        {
            // Arrange
            var responseBody = @"retry_after: 0";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 429, responseBody);

            // Assert
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Equal(TimeSpan.Zero, rateLimitEx.RetryAfter);
        }

        [Fact]
        public void MapHttpError_429_WithScientificNotation_ParsesLeadingDigits()
        {
            // Arrange - Regex matches leading digits before 'e' (matches "1" from "1e5")
            var responseBody = @"retry_after: 1e5";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 429, responseBody);

            // Assert - The regex \d+ matches "1" before the 'e'
            var rateLimitEx = Assert.IsType<RateLimitException>(result);
            Assert.Equal(TimeSpan.FromSeconds(1), rateLimitEx.RetryAfter);
        }

        #endregion

        #region Error Message Parsing Edge Cases

        [Fact]
        public void MapHttpError_400_WithNestedJsonMessage_ParsesFirstMatch()
        {
            // Arrange - Line 98-101: regex finds first match
            var responseBody = @"{""outer"": {""message"": ""nested message""}}";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 400, responseBody);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal("nested message", providerEx.Message);
        }

        [Fact]
        public void MapHttpError_400_WithBothMessageAndError_ParsesMessage()
        {
            // Arrange
            var responseBody = @"{""message"": ""msg"", ""error"": ""err""}";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 400, responseBody);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal("msg", providerEx.Message);
        }

        [Fact]
        public void MapHttpError_400_WithSingleQuotedMessage_ParsesMessage()
        {
            // Arrange - Line 100: regex uses ["'] for quotes
            var responseBody = @"{'message': 'single quoted'}";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 400, responseBody);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal("single quoted", providerEx.Message);
        }

        [Fact]
        public void MapHttpError_400_WithMixedQuotes_ParsesMessage()
        {
            // Arrange
            var responseBody = @"{""message"": 'mixed quotes'}";

            // Act
            var result = LlmErrorMapper.MapHttpError("test-provider", 400, responseBody);

            // Assert
            var providerEx = Assert.IsType<ProviderException>(result);
            Assert.Equal("mixed quotes", providerEx.Message);
        }

        #endregion
    }
}
