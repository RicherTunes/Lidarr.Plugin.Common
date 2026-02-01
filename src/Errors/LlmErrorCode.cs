namespace Lidarr.Plugin.Common.Errors;

/// <summary>
/// Normalized error codes for LLM provider operations.
/// Grouped by category: Authentication (100s), RateLimit (200s), Provider (300s), Network (400s).
/// </summary>
public enum LlmErrorCode
{
    Unknown = 0,

    // Authentication errors (INFRA-05: AuthenticationError)
    AuthenticationFailed = 100,
    AuthorizationFailed = 101,
    CredentialsExpired = 102,
    QuotaExceeded = 103,

    // Rate limiting errors (INFRA-05: RateLimitError)
    RateLimited = 200,
    ConcurrencyLimitExceeded = 201,

    // Provider errors (INFRA-05: ProviderError)
    ProviderUnavailable = 300,
    ProviderOverloaded = 301,
    ModelNotFound = 302,
    ContextLengthExceeded = 303,
    ContentFiltered = 304,
    InvalidRequest = 305,

    // Network errors (INFRA-05: NetworkError)
    NetworkError = 400,
    Timeout = 401,
    ConnectionFailed = 402
}
