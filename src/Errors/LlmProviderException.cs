using System;

namespace Lidarr.Plugin.Common.Errors;

/// <summary>
/// Base exception for all LLM provider errors.
/// Contains metadata for error handling: provider ID, error code, retryability, retry timing.
/// </summary>
public abstract class LlmProviderException : Exception
{
    /// <summary>Identifier of the provider that threw this exception.</summary>
    public string ProviderId { get; }

    /// <summary>Normalized error code for programmatic handling.</summary>
    public LlmErrorCode ErrorCode { get; }

    /// <summary>Whether this error is transient and the request can be retried.</summary>
    public bool IsRetryable { get; }

    /// <summary>Suggested wait time before retry, if known from provider response.</summary>
    public TimeSpan? RetryAfter { get; }

    protected LlmProviderException(
        string providerId,
        LlmErrorCode errorCode,
        string message,
        Exception? innerException = null,
        bool isRetryable = false,
        TimeSpan? retryAfter = null)
        : base(message, innerException)
    {
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        ErrorCode = errorCode;
        IsRetryable = isRetryable;
        RetryAfter = retryAfter;
    }
}
