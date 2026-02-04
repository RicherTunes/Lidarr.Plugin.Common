// <copyright file="ProviderHealthResult.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Represents the result of an LLM provider health check.
/// Returned by <see cref="ILlmProvider.CheckHealthAsync"/> to indicate
/// whether the provider is ready to accept requests.
/// </summary>
/// <remarks>
/// <para>
/// This record extends the basic health check with DIAG-02 fields that provide
/// additional diagnostic information for troubleshooting provider issues.
/// </para>
/// <para>
/// All fields are nullable to maintain backward compatibility with existing
/// code that doesn't use the extended fields.
/// </para>
/// </remarks>
public record ProviderHealthResult
{
    /// <summary>
    /// Gets a value indicating whether the provider is healthy and ready.
    /// When true, the provider can accept requests.
    /// When false, check <see cref="StatusMessage"/> for details.
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Gets an optional message describing the health status.
    /// Particularly useful for unhealthy or degraded states to explain the issue.
    /// </summary>
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Gets the time taken to perform the health check.
    /// Useful for monitoring provider latency and performance.
    /// </summary>
    public TimeSpan? ResponseTime { get; init; }

    /// <summary>
    /// Gets the name/identifier of the LLM provider (e.g., 'openai', 'claude-code', 'glm').
    /// </summary>
    /// <remarks>
    /// This field enables logging and UI to identify which provider failed or returned
    /// a specific status. Useful for debugging when multiple providers are configured.
    /// </remarks>
    /// <example>
    /// "openai", "claude-code", "glm", "gemini", "anthropic"
    /// </example>
    public string? Provider { get; init; }

    /// <summary>
    /// Gets the authentication method used by the provider.
    /// </summary>
    /// <remarks>
    /// Indicates how the provider authenticates with the API (e.g., API key, device code flow,
    /// CLI, OAuth, etc.). This helps diagnose authentication-related failures.
    /// </remarks>
    /// <example>
    /// "apiKey", "cli", "deviceCode", "oauth", "none" (if not configured)
    /// </example>
    public string? AuthMethod { get; init; }

    /// <summary>
    /// Gets the model identifier used for this health check.
    /// </summary>
    /// <remarks>
    /// If the provider supports multiple models, this specifies which model was used.
    /// Helps correlate health checks with specific model configurations.
    /// </remarks>
    /// <example>
    /// "gpt-4", "claude-3-opus", "glm-4-flash"
    /// </example>
    public string? Model { get; init; }

    /// <summary>
    /// Gets the error code if the provider check failed.
    /// </summary>
    /// <remarks>
    /// Provides a standardized error identifier that can be used for:
    /// - Automated issue triage and routing
    /// - Error message translation
    /// - Alert thresholding and monitoring
    /// </remarks>
    /// <example>
    /// "AUTH_FAILED", "RATE_LIMITED", "CONNECTION_FAILED", "TIMEOUT", "INVALID_RESPONSE"
    /// </example>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Creates a healthy result indicating the provider is ready.
    /// </summary>
    /// <param name="responseTime">Optional response time from the health check.</param>
    /// <param name="provider">Optional provider identifier (e.g., 'openai', 'claude-code').</param>
    /// <param name="authMethod">Optional authentication method (e.g., 'apiKey', 'cli').</param>
    /// <param name="model">Optional model identifier (e.g., 'gpt-4', 'glm-4-flash').</param>
    /// <returns>A healthy <see cref="ProviderHealthResult"/>.</returns>
    public static ProviderHealthResult Healthy(
        TimeSpan? responseTime = null,
        string? provider = null,
        string? authMethod = null,
        string? model = null) => new()
    {
        IsHealthy = true,
        StatusMessage = null,
        ResponseTime = responseTime,
        Provider = provider,
        AuthMethod = authMethod,
        Model = model
    };

    /// <summary>
    /// Creates an unhealthy result indicating the provider cannot accept requests.
    /// </summary>
    /// <param name="reason">The reason the provider is unhealthy.</param>
    /// <param name="responseTime">Optional response time from the health check.</param>
    /// <param name="provider">Optional provider identifier.</param>
    /// <param name="authMethod">Optional authentication method.</param>
    /// <param name="model">Optional model identifier.</param>
    /// <param name="errorCode">Optional error code for diagnostics.</param>
    /// <returns>An unhealthy <see cref="ProviderHealthResult"/>.</returns>
    public static ProviderHealthResult Unhealthy(
        string reason,
        TimeSpan? responseTime = null,
        string? provider = null,
        string? authMethod = null,
        string? model = null,
        string? errorCode = null) => new()
    {
        IsHealthy = false,
        StatusMessage = reason,
        ResponseTime = responseTime,
        Provider = provider,
        AuthMethod = authMethod,
        Model = model,
        ErrorCode = errorCode
    };

    /// <summary>
    /// Creates a degraded result indicating the provider is functional but impaired.
    /// Degraded providers can still accept requests but may have reduced performance.
    /// </summary>
    /// <param name="reason">The reason the provider is degraded.</param>
    /// <param name="responseTime">Optional response time from the health check.</param>
    /// <param name="provider">Optional provider identifier.</param>
    /// <param name="authMethod">Optional authentication method.</param>
    /// <param name="model">Optional model identifier.</param>
    /// <returns>A degraded <see cref="ProviderHealthResult"/> (marked as healthy but with warning message).</returns>
    public static ProviderHealthResult Degraded(
        string reason,
        TimeSpan? responseTime = null,
        string? provider = null,
        string? authMethod = null,
        string? model = null) => new()
    {
        IsHealthy = true,
        StatusMessage = $"[Degraded] {reason}",
        ResponseTime = responseTime,
        Provider = provider,
        AuthMethod = authMethod,
        Model = model
    };
}
