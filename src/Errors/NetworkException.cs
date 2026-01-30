using System;

namespace Lidarr.Plugin.Common.Errors;

/// <summary>
/// Thrown when network-level errors occur (connection failed, timeout, DNS failure).
/// Always retryable as these are typically transient.
/// </summary>
public sealed class NetworkException : LlmProviderException
{
    public NetworkException(string providerId, string message, Exception? inner = null)
        : base(providerId, LlmErrorCode.NetworkError, message, inner, isRetryable: true)
    { }

    public NetworkException(string providerId, LlmErrorCode code, string message, Exception? inner = null)
        : base(providerId, code, message, inner, isRetryable: true)
    { }
}
