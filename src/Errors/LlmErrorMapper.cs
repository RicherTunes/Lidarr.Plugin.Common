using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Errors;

/// <summary>
/// Maps HTTP status codes and provider-specific errors to normalized exception types.
/// Use this in provider implementations to normalize errors at the boundary.
/// </summary>
public static class LlmErrorMapper
{
    /// <summary>
    /// Maps an HTTP status code to the appropriate LlmProviderException type.
    /// </summary>
    /// <param name="providerId">The provider that returned this error.</param>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="responseBody">Optional response body for parsing retry-after or error details.</param>
    /// <param name="inner">Optional inner exception.</param>
    /// <returns>An appropriate LlmProviderException subclass.</returns>
    public static LlmProviderException MapHttpError(
        string providerId,
        int statusCode,
        string? responseBody = null,
        Exception? inner = null)
    {
        return statusCode switch
        {
            401 => new AuthenticationException(providerId, "Invalid API key or credentials", inner),
            403 => new AuthenticationException(providerId, LlmErrorCode.AuthorizationFailed, "Access denied - check API key permissions", inner),
            429 => new RateLimitException(providerId, "Rate limit exceeded", ParseRetryAfter(responseBody)),
            400 => new ProviderException(providerId, LlmErrorCode.InvalidRequest, ParseErrorMessage(responseBody) ?? "Invalid request", inner),
            404 => new ProviderException(providerId, LlmErrorCode.ModelNotFound, "Model or endpoint not found", inner),
            500 => new ProviderException(providerId, LlmErrorCode.ProviderUnavailable, "Internal server error", inner),
            502 => new ProviderException(providerId, LlmErrorCode.ProviderUnavailable, "Bad gateway", inner),
            503 => new ProviderException(providerId, LlmErrorCode.ProviderOverloaded, "Service unavailable - provider may be overloaded", inner),
            504 => new NetworkException(providerId, LlmErrorCode.Timeout, "Gateway timeout", inner),
            _ => new ProviderException(providerId, LlmErrorCode.Unknown, $"Unexpected HTTP error: {statusCode}", inner)
        };
    }

    /// <summary>
    /// Maps a .NET exception to the appropriate LlmProviderException type.
    /// Use for wrapping HttpClient and other I/O exceptions.
    /// </summary>
    public static LlmProviderException MapException(string providerId, Exception ex)
    {
        return ex switch
        {
            TaskCanceledException tce when tce.CancellationToken.IsCancellationRequested
                => new NetworkException(providerId, LlmErrorCode.Timeout, "Request was cancelled", tce),
            TaskCanceledException tce
                => new NetworkException(providerId, LlmErrorCode.Timeout, "Request timed out", tce),
            HttpRequestException hre when hre.StatusCode.HasValue
                => MapHttpError(providerId, (int)hre.StatusCode.Value, null, hre),
            HttpRequestException hre
                => new NetworkException(providerId, LlmErrorCode.ConnectionFailed, $"Connection failed: {hre.Message}", hre),
            OperationCanceledException oce
                => new NetworkException(providerId, LlmErrorCode.Timeout, "Operation was cancelled", oce),
            _ => new ProviderException(providerId, LlmErrorCode.Unknown, $"Unexpected error: {ex.Message}", ex)
        };
    }

    /// <summary>
    /// Parses the retry-after value from a response body or headers.
    /// Returns null if not found or unparseable.
    /// </summary>
    private static TimeSpan? ParseRetryAfter(string? responseBody)
    {
        if (string.IsNullOrEmpty(responseBody))
            return null;

        // Try to find retry_after or retry-after in JSON response
        // Simple regex approach - providers can override with more sophisticated parsing
        var match = Regex.Match(
            responseBody,
            @"[""']?retry[-_]?after[""']?\s*[:=]\s*(\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase);

        if (match.Success && double.TryParse(match.Groups[1].Value, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    /// <summary>
    /// Extracts error message from response body if available.
    /// </summary>
    private static string? ParseErrorMessage(string? responseBody)
    {
        if (string.IsNullOrEmpty(responseBody))
            return null;

        // Try to extract "message" or "error" field from JSON
        var match = Regex.Match(
            responseBody,
            @"[""'](?:message|error)[""']\s*:\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value : null;
    }
}
