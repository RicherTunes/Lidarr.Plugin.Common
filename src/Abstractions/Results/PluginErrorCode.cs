namespace Lidarr.Plugin.Abstractions.Results
{
    /// <summary>
    /// Standardised error codes returned by plugin operations.
    /// </summary>
    public enum PluginErrorCode
    {
        None = 0,
        Unknown,
        ValidationFailed,
        NotFound,
        Unauthorized,
        AuthenticationExpired,
        RateLimited,
        Timeout,
        Cancelled,
        ProviderUnavailable,
        Unsupported,
        QuotaExceeded,
        Conflict,
        ParsingFailed,
        NetworkFailure
    }
}
