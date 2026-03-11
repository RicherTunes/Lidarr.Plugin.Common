using System;

namespace Lidarr.Plugin.Common.Errors;

/// <summary>
/// Thrown when rate limit is exceeded.
/// Corresponds to HTTP 429. Always retryable with optional retry-after.
/// </summary>
public sealed class RateLimitException : LlmProviderException
{
    public RateLimitException(string providerId, string message, TimeSpan? retryAfter = null)
        : base(providerId, LlmErrorCode.RateLimited, message, innerException: null, isRetryable: true, retryAfter)
    { }

    public RateLimitException(string providerId, LlmErrorCode code, string message, TimeSpan? retryAfter = null)
        : base(providerId, code, message, innerException: null, isRetryable: true, retryAfter)
    { }
}
