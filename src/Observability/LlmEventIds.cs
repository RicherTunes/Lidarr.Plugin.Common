using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Observability;

/// <summary>
/// Standard event IDs for LLM provider operations.
/// Use these with ILogger to enable consistent log filtering and alerting.
/// Event IDs are grouped by category:
/// - 2000-2009: Authentication events
/// - 2010-2019: Request lifecycle events
/// - 2020-2029: Rate limiting events
/// - 2030-2039: Health check events
/// </summary>
public static class LlmEventIds
{
    // Authentication events (INFRA-02)
    /// <summary>Authentication succeeded (API key valid, OAuth token accepted).</summary>
    public static readonly EventId AUTH_SUCCESS = new(2000, "AUTH_SUCCESS");

    /// <summary>Authentication failed (invalid key, expired token, unauthorized).</summary>
    public static readonly EventId AUTH_FAIL = new(2001, "AUTH_FAIL");

    // Request lifecycle events (INFRA-02)
    /// <summary>LLM request started (prompt sent to provider).</summary>
    public static readonly EventId REQUEST_START = new(2010, "REQUEST_START");

    /// <summary>LLM request completed successfully.</summary>
    public static readonly EventId REQUEST_COMPLETE = new(2011, "REQUEST_COMPLETE");

    /// <summary>LLM request failed with error.</summary>
    public static readonly EventId REQUEST_ERROR = new(2012, "REQUEST_ERROR");

    // Rate limiting events (INFRA-02)
    /// <summary>Rate limit hit (HTTP 429 or equivalent).</summary>
    public static readonly EventId RATE_LIMITED = new(2020, "RATE_LIMITED");

    /// <summary>Recovered from rate limit (successful request after backoff).</summary>
    public static readonly EventId RATE_LIMIT_RECOVERED = new(2021, "RATE_LIMIT_RECOVERED");

    // Health check events
    /// <summary>Provider health check passed.</summary>
    public static readonly EventId HEALTH_CHECK_PASS = new(2030, "HEALTH_CHECK_PASS");

    /// <summary>Provider health check failed.</summary>
    public static readonly EventId HEALTH_CHECK_FAIL = new(2031, "HEALTH_CHECK_FAIL");
}
