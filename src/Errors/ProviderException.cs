using System;

namespace Lidarr.Plugin.Common.Errors;

/// <summary>
/// Thrown when the provider reports an error (5xx, invalid request, content filtered, etc.).
/// Retryability depends on the specific error code.
/// </summary>
public sealed class ProviderException : LlmProviderException
{
    public ProviderException(string providerId, LlmErrorCode code, string message, Exception? inner = null)
        : base(providerId, code, message, inner, isRetryable: IsCodeRetryable(code))
    { }

    private static bool IsCodeRetryable(LlmErrorCode code) => code switch
    {
        LlmErrorCode.ProviderUnavailable => true,
        LlmErrorCode.ProviderOverloaded => true,
        _ => false
    };
}
