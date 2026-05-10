using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Observability;

public class LlmLoggerExtensionsTests
{
    private readonly RecordingLogger _logger = new();

    // Authentication

    [Fact]
    public void LogAuthSuccess_EmitsInfoWithExpectedEventId()
    {
        _logger.LogAuthSuccess("brainarr", "openai", "corr-1");
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(LlmEventIds.AUTH_SUCCESS.Id, entry.EventId.Id);
        Assert.Contains("brainarr", entry.RenderedMessage);
        Assert.Contains("openai", entry.RenderedMessage);
        Assert.Contains("corr-1", entry.RenderedMessage);
    }

    [Fact]
    public void LogAuthFail_EmitsWarningAndRedactsReason()
    {
        var leakedReason = "auth failed for token sk-abcdef1234567890ABCDEF1234567890";
        _logger.LogAuthFail("brainarr", "anthropic", "corr-2", leakedReason);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(LlmEventIds.AUTH_FAIL.Id, entry.EventId.Id);
        Assert.DoesNotContain("sk-abcdef1234567890ABCDEF1234567890", entry.RenderedMessage);
        Assert.Contains("REDACTED", entry.RenderedMessage);
    }

    // Request lifecycle

    [Fact]
    public void LogRequestStart_DefaultsModelAndAttempt()
    {
        _logger.LogRequestStart("brainarr", "openai", "complete", "corr-3");
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(LlmEventIds.REQUEST_START.Id, entry.EventId.Id);
        Assert.Contains("Model=default", entry.RenderedMessage);
        Assert.Contains("Attempt=1", entry.RenderedMessage);
    }

    [Fact]
    public void LogRequestStart_WithExplicitModelAndAttempt()
    {
        _logger.LogRequestStart("brainarr", "openai", "complete", "corr-4", model: "gpt-4o", attempt: 3);
        var entry = Assert.Single(_logger.Entries);
        Assert.Contains("Model=gpt-4o", entry.RenderedMessage);
        Assert.Contains("Attempt=3", entry.RenderedMessage);
    }

    [Fact]
    public void LogRequestComplete_NullTokens_RenderedAsZero()
    {
        // Documented behavior: nullable token counts default to 0 when absent so dashboards don't blow up.
        _logger.LogRequestComplete("brainarr", "openai", "complete", "corr-5", elapsedMs: 250);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LlmEventIds.REQUEST_COMPLETE.Id, entry.EventId.Id);
        Assert.Contains("ElapsedMs=250", entry.RenderedMessage);
        Assert.Contains("InputTokens=0", entry.RenderedMessage);
        Assert.Contains("OutputTokens=0", entry.RenderedMessage);
    }

    [Fact]
    public void LogRequestComplete_WithTokens_RenderedFromArguments()
    {
        _logger.LogRequestComplete("brainarr", "openai", "complete", "corr-6", 250, inputTokens: 100, outputTokens: 250);
        var entry = Assert.Single(_logger.Entries);
        Assert.Contains("InputTokens=100", entry.RenderedMessage);
        Assert.Contains("OutputTokens=250", entry.RenderedMessage);
    }

    [Fact]
    public void LogRequestError_RedactsErrorMessageAndAttachesException()
    {
        var ex = new InvalidOperationException("Bearer eyJsecret.payload.sig is invalid");
        _logger.LogRequestError("brainarr", "openai", "complete", "corr-7", "ERR_AUTH", "secret token leaked: sk-abcdef1234567890ABCDEF1234567890", ex);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(LlmEventIds.REQUEST_ERROR.Id, entry.EventId.Id);
        Assert.Same(ex, entry.Exception);
        Assert.Contains("ERR_AUTH", entry.RenderedMessage);
        Assert.DoesNotContain("sk-abcdef1234567890ABCDEF1234567890", entry.RenderedMessage);
        Assert.Contains("REDACTED", entry.RenderedMessage);
    }

    // Rate limiting

    [Fact]
    public void LogRateLimited_NullRetryAfter_RendersNegativeOne()
    {
        // Sentinel for "no Retry-After header was present" — easier to filter in dashboards.
        _logger.LogRateLimited("brainarr", "openai", "corr-8");
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(LlmEventIds.RATE_LIMITED.Id, entry.EventId.Id);
        Assert.Contains("RetryAfterMs=-1", entry.RenderedMessage);
    }

    [Fact]
    public void LogRateLimited_WithRetryAfter_RendersTotalMilliseconds()
    {
        _logger.LogRateLimited("brainarr", "openai", "corr-9", retryAfter: TimeSpan.FromSeconds(2));
        var entry = Assert.Single(_logger.Entries);
        Assert.Contains("RetryAfterMs=2000", entry.RenderedMessage);
    }

    [Fact]
    public void LogRateLimitRecovered_EmitsInfoWithAttempts()
    {
        _logger.LogRateLimitRecovered("brainarr", "openai", "corr-10", totalAttempts: 4);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(LlmEventIds.RATE_LIMIT_RECOVERED.Id, entry.EventId.Id);
        Assert.Contains("TotalAttempts=4", entry.RenderedMessage);
    }

    // Health check

    [Fact]
    public void LogHealthCheckPass_EmitsInfoWithElapsed()
    {
        _logger.LogHealthCheckPass("brainarr", "openai", elapsedMs: 42);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(LlmEventIds.HEALTH_CHECK_PASS.Id, entry.EventId.Id);
        Assert.Contains("ElapsedMs=42", entry.RenderedMessage);
    }

    [Fact]
    public void LogHealthCheckFail_RedactsReason()
    {
        _logger.LogHealthCheckFail("brainarr", "openai", "Authorization: Bearer eyJleak.payload.sig was rejected");
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(LlmEventIds.HEALTH_CHECK_FAIL.Id, entry.EventId.Id);
        Assert.DoesNotContain("eyJleak.payload.sig", entry.RenderedMessage);
        Assert.Contains("REDACTED", entry.RenderedMessage);
    }

    // EventId allocation: each event must have a distinct ID so log filters can target individual events.
    [Fact]
    public void EventIds_AllDistinct()
    {
        var ids = new[]
        {
            LlmEventIds.AUTH_SUCCESS.Id,
            LlmEventIds.AUTH_FAIL.Id,
            LlmEventIds.REQUEST_START.Id,
            LlmEventIds.REQUEST_COMPLETE.Id,
            LlmEventIds.REQUEST_ERROR.Id,
            LlmEventIds.RATE_LIMITED.Id,
            LlmEventIds.RATE_LIMIT_RECOVERED.Id,
            LlmEventIds.HEALTH_CHECK_PASS.Id,
            LlmEventIds.HEALTH_CHECK_FAIL.Id,
        };
        Assert.Equal(ids.Length, new HashSet<int>(ids).Count);
    }

    // Recording logger

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(level, eventId, formatter(state, exception), exception));
        }

        public sealed record LogEntry(LogLevel Level, EventId EventId, string RenderedMessage, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
