using System;

namespace Lidarr.Plugin.Common.Errors;

/// <summary>
/// Thrown when authentication or authorization fails.
/// Corresponds to HTTP 401/403 or invalid API key errors.
/// </summary>
public sealed class AuthenticationException : LlmProviderException
{
    public AuthenticationException(string providerId, string message, Exception? inner = null)
        : base(providerId, LlmErrorCode.AuthenticationFailed, message, inner, isRetryable: false)
    { }

    public AuthenticationException(string providerId, LlmErrorCode code, string message, Exception? inner = null)
        : base(providerId, code, message, inner, isRetryable: false)
    { }
}
