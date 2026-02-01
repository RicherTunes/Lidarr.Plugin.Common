using System;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Observability;

/// <summary>
/// Extension methods for structured LLM operation logging.
/// All methods include standard fields: plugin, provider, operation, correlationId.
/// Use these instead of raw ILogger methods to ensure consistent log format.
/// </summary>
public static class LlmLoggerExtensions
{
    // ============================================================
    // Authentication Events (INFRA-02, INFRA-03)
    // ============================================================

    /// <summary>
    /// Log successful authentication.
    /// </summary>
    public static void LogAuthSuccess(
        this ILogger logger,
        string plugin,
        string provider,
        string correlationId)
    {
        logger.LogInformation(
            LlmEventIds.AUTH_SUCCESS,
            "Authentication successful: {Plugin} {Provider} {CorrelationId}",
            plugin, provider, correlationId);
    }

    /// <summary>
    /// Log authentication failure.
    /// </summary>
    public static void LogAuthFail(
        this ILogger logger,
        string plugin,
        string provider,
        string correlationId,
        string reason)
    {
        logger.LogWarning(
            LlmEventIds.AUTH_FAIL,
            "Authentication failed: {Plugin} {Provider} {CorrelationId} Reason={Reason}",
            plugin, provider, correlationId, LogRedactor.Redact(reason));
    }

    // ============================================================
    // Request Lifecycle Events (INFRA-02, INFRA-03)
    // ============================================================

    /// <summary>
    /// Log request start with all standard fields.
    /// </summary>
    public static void LogRequestStart(
        this ILogger logger,
        string plugin,
        string provider,
        string operation,
        string correlationId,
        string? model = null,
        int attempt = 1)
    {
        logger.LogInformation(
            LlmEventIds.REQUEST_START,
            "Request started: {Plugin} {Provider} {Operation} {CorrelationId} Model={Model} Attempt={Attempt}",
            plugin, provider, operation, correlationId, model ?? "default", attempt);
    }

    /// <summary>
    /// Log successful request completion with timing and token usage.
    /// </summary>
    public static void LogRequestComplete(
        this ILogger logger,
        string plugin,
        string provider,
        string operation,
        string correlationId,
        long elapsedMs,
        int? inputTokens = null,
        int? outputTokens = null)
    {
        logger.LogInformation(
            LlmEventIds.REQUEST_COMPLETE,
            "Request completed: {Plugin} {Provider} {Operation} {CorrelationId} ElapsedMs={ElapsedMs} InputTokens={InputTokens} OutputTokens={OutputTokens}",
            plugin, provider, operation, correlationId, elapsedMs, inputTokens ?? 0, outputTokens ?? 0);
    }

    /// <summary>
    /// Log request error with error code.
    /// </summary>
    public static void LogRequestError(
        this ILogger logger,
        string plugin,
        string provider,
        string operation,
        string correlationId,
        string errorCode,
        string errorMessage,
        Exception? exception = null)
    {
        logger.LogError(
            LlmEventIds.REQUEST_ERROR,
            exception,
            "Request error: {Plugin} {Provider} {Operation} {CorrelationId} ErrorCode={ErrorCode} Error={Error}",
            plugin, provider, operation, correlationId, errorCode, LogRedactor.Redact(errorMessage));
    }

    // ============================================================
    // Rate Limiting Events (INFRA-02, INFRA-03)
    // ============================================================

    /// <summary>
    /// Log rate limit hit.
    /// </summary>
    public static void LogRateLimited(
        this ILogger logger,
        string plugin,
        string provider,
        string correlationId,
        TimeSpan? retryAfter = null)
    {
        logger.LogWarning(
            LlmEventIds.RATE_LIMITED,
            "Rate limited: {Plugin} {Provider} {CorrelationId} RetryAfterMs={RetryAfterMs}",
            plugin, provider, correlationId, retryAfter?.TotalMilliseconds ?? -1);
    }

    /// <summary>
    /// Log recovery from rate limit (first successful request after being rate limited).
    /// </summary>
    public static void LogRateLimitRecovered(
        this ILogger logger,
        string plugin,
        string provider,
        string correlationId,
        int totalAttempts)
    {
        logger.LogInformation(
            LlmEventIds.RATE_LIMIT_RECOVERED,
            "Rate limit recovered: {Plugin} {Provider} {CorrelationId} TotalAttempts={TotalAttempts}",
            plugin, provider, correlationId, totalAttempts);
    }

    // ============================================================
    // Health Check Events
    // ============================================================

    /// <summary>
    /// Log health check pass.
    /// </summary>
    public static void LogHealthCheckPass(
        this ILogger logger,
        string plugin,
        string provider,
        long elapsedMs)
    {
        logger.LogInformation(
            LlmEventIds.HEALTH_CHECK_PASS,
            "Health check passed: {Plugin} {Provider} ElapsedMs={ElapsedMs}",
            plugin, provider, elapsedMs);
    }

    /// <summary>
    /// Log health check failure.
    /// </summary>
    public static void LogHealthCheckFail(
        this ILogger logger,
        string plugin,
        string provider,
        string reason)
    {
        logger.LogWarning(
            LlmEventIds.HEALTH_CHECK_FAIL,
            "Health check failed: {Plugin} {Provider} Reason={Reason}",
            plugin, provider, LogRedactor.Redact(reason));
    }
}
