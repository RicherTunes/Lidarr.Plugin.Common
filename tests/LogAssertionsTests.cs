using System;
using Lidarr.Plugin.Common.TestKit.Assertions;
using Lidarr.Plugin.Common.TestKit.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Contract tests for log security assertions.
/// These tests document the expected behavior for secret detection in logs
/// and should be used as a reference by all plugins.
/// </summary>
public class LogAssertionsTests
{
    #region AssertNoSecretsInLogs

    [Fact]
    public void AssertNoSecretsInLogs_NoSecrets_Passes()
    {
        // Arrange
        var sink = new TestLogSink();
        sink.Add(new TestLogEntry("Test", LogLevel.Information, "User logged in successfully", null));
        sink.Add(new TestLogEntry("Test", LogLevel.Debug, "Processing request for album 12345", null));

        // Act & Assert - should not throw
        LogAssertions.AssertNoSecretsInLogs(sink, "my-api-key", "my-secret-token");
    }

    [Fact]
    public void AssertNoSecretsInLogs_SecretInMessage_Throws()
    {
        // Arrange
        var sink = new TestLogSink();
        var apiKey = "sk_live_abc123xyz";
        sink.Add(new TestLogEntry("ApiClient", LogLevel.Error, $"Failed to authenticate with key {apiKey}", null));

        // Act & Assert
        var ex = Assert.Throws<LogAssertionException>(() =>
            LogAssertions.AssertNoSecretsInLogs(sink, apiKey));

        Assert.Contains("secret", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Message", ex.Message);
    }

    [Fact]
    public void AssertNoSecretsInLogs_SecretInExceptionMessage_Throws()
    {
        // Arrange
        var sink = new TestLogSink();
        var token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
        var exception = new Exception($"Authentication failed for token: {token}");
        sink.Add(new TestLogEntry("Auth", LogLevel.Error, "Login failed", exception));

        // Act & Assert
        var ex = Assert.Throws<LogAssertionException>(() =>
            LogAssertions.AssertNoSecretsInLogs(sink, token));

        Assert.Contains("Exception", ex.Message);
    }

    [Fact]
    public void AssertNoSecretsInLogs_EmptySecretsList_Passes()
    {
        // Arrange
        var sink = new TestLogSink();
        sink.Add(new TestLogEntry("Test", LogLevel.Information, "Some message", null));

        // Act & Assert - should not throw with empty secrets
        LogAssertions.AssertNoSecretsInLogs(sink);
        LogAssertions.AssertNoSecretsInLogs(sink, Array.Empty<string>());
    }

    [Fact]
    public void AssertNoSecretsInLogs_NullSink_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LogAssertions.AssertNoSecretsInLogs(null!, "secret"));
    }

    [Fact]
    public void AssertNoSecretsInLogs_MasksSecretInErrorMessage()
    {
        // Arrange
        var sink = new TestLogSink();
        var longSecret = "verylongsecretkey12345";
        sink.Add(new TestLogEntry("Test", LogLevel.Error, $"Token: {longSecret}", null));

        // Act & Assert
        var ex = Assert.Throws<LogAssertionException>(() =>
            LogAssertions.AssertNoSecretsInLogs(sink, longSecret));

        // Secret should be masked (showing first 2 and last 2 chars)
        Assert.Contains("ve...45", ex.Message);
        Assert.DoesNotContain(longSecret, ex.Message);
    }

    #endregion

    #region AssertNoUnredactedUrls

    [Fact]
    public void AssertNoUnredactedUrls_NoUrls_Passes()
    {
        // Arrange
        var sink = new TestLogSink();
        sink.Add(new TestLogEntry("Test", LogLevel.Information, "Processing complete", null));

        // Act & Assert
        LogAssertions.AssertNoUnredactedUrls(sink);
    }

    [Fact]
    public void AssertNoUnredactedUrls_RedactedUrl_Passes()
    {
        // Arrange
        var sink = new TestLogSink();
        sink.Add(new TestLogEntry("Http", LogLevel.Debug, "Request to https://api.example.com/track?[REDACTED]", null));

        // Act & Assert
        LogAssertions.AssertNoUnredactedUrls(sink);
    }

    [Fact]
    public void AssertNoUnredactedUrls_UrlWithoutQuery_Passes()
    {
        // Arrange
        var sink = new TestLogSink();
        sink.Add(new TestLogEntry("Http", LogLevel.Debug, "Request to https://api.example.com/track/123", null));

        // Act & Assert
        LogAssertions.AssertNoUnredactedUrls(sink);
    }

    [Fact]
    public void AssertNoUnredactedUrls_UnredactedQueryString_Throws()
    {
        // Arrange
        var sink = new TestLogSink();
        sink.Add(new TestLogEntry("Http", LogLevel.Debug, "Request to https://api.example.com/track?token=secret123&id=456", null));

        // Act & Assert
        var ex = Assert.Throws<LogAssertionException>(() =>
            LogAssertions.AssertNoUnredactedUrls(sink));

        Assert.Contains("unredacted URL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region AssertNoBearerTokensInLogs

    [Fact]
    public void AssertNoBearerTokensInLogs_NoBearerTokens_Passes()
    {
        // Arrange
        var sink = new TestLogSink();
        sink.Add(new TestLogEntry("Http", LogLevel.Debug, "Authorization header set", null));

        // Act & Assert
        LogAssertions.AssertNoBearerTokensInLogs(sink);
    }

    [Fact]
    public void AssertNoBearerTokensInLogs_RedactedBearer_Passes()
    {
        // Arrange
        var sink = new TestLogSink();
        sink.Add(new TestLogEntry("Http", LogLevel.Debug, "Auth: Bearer [REDACTED]", null));

        // Act & Assert
        LogAssertions.AssertNoBearerTokensInLogs(sink);
    }

    [Fact]
    public void AssertNoBearerTokensInLogs_UnredactedBearer_Throws()
    {
        // Arrange
        var sink = new TestLogSink();
        sink.Add(new TestLogEntry("Http", LogLevel.Debug, "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", null));

        // Act & Assert
        var ex = Assert.Throws<LogAssertionException>(() =>
            LogAssertions.AssertNoBearerTokensInLogs(sink));

        Assert.Contains("Bearer token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region AssertSecureLogs (All-in-One)

    [Fact]
    public void AssertSecureLogs_AllClean_Passes()
    {
        // Arrange
        var sink = new TestLogSink();
        sink.Add(new TestLogEntry("App", LogLevel.Information, "Application started", null));
        sink.Add(new TestLogEntry("Http", LogLevel.Debug, "Request to https://api.example.com/track/123", null));

        // Act & Assert
        LogAssertions.AssertSecureLogs(sink, "my-secret-key");
    }

    [Fact]
    public void AssertSecureLogs_MultipleViolations_ThrowsOnFirst()
    {
        // Arrange
        var sink = new TestLogSink();
        var secret = "my-secret-key";
        sink.Add(new TestLogEntry("Auth", LogLevel.Error, $"Using key: {secret}", null));

        // Act & Assert
        Assert.Throws<LogAssertionException>(() =>
            LogAssertions.AssertSecureLogs(sink, secret));
    }

    #endregion

    #region Integration Example - How Plugins Should Use This

    /// <summary>
    /// Example test showing how plugins should use LogAssertions in their tests.
    /// This pattern should be followed by Qobuzarr, Tidalarr, Brainarr, etc.
    /// </summary>
    [Fact]
    public void Example_PluginLogSecurityTest()
    {
        // Arrange - Create a test context that captures logs
        using var context = new PluginTestContext(new Version(2, 13, 0));
        var logger = context.LoggerFactory.CreateLogger("ExampleService");

        // Simulate plugin operations that might log sensitive data
        var apiKey = "test-api-key-12345";
        var safeMessage = "Processing request for user profile";

        // Act - Log something (simulating normal plugin operation)
        logger.LogInformation(safeMessage);

        // Assert - Verify no secrets leaked
        LogAssertions.AssertNoSecretsInLogs(context.LogEntries, apiKey);
    }

    /// <summary>
    /// Example showing how to verify a service correctly redacts sensitive data.
    /// </summary>
    [Fact]
    public void Example_VerifyServiceRedactsSensitiveData()
    {
        // Arrange
        using var context = new PluginTestContext(new Version(2, 13, 0));
        var logger = context.LoggerFactory.CreateLogger("HttpClient");

        // Simulate logging a URL that should be redacted
        var url = "https://api.qobuz.com/track?app_id=123&token=secret";
        var redactedUrl = Lidarr.Plugin.Common.Security.Sanitize.RedactUrls(url);

        // Act - Log the REDACTED url (correct behavior)
        logger.LogDebug("Request to {Url}", redactedUrl);

        // Assert - Verify no unredacted URLs in logs
        LogAssertions.AssertNoUnredactedUrls(context.LogEntries);
    }

    #endregion
}
