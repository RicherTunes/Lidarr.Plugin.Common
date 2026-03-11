// <copyright file="DiagnosticHealthResult.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using Lidarr.Plugin.Common.Abstractions.Llm;

namespace Lidarr.Plugin.Common.Abstractions.Diagnostics;

/// <summary>
/// Represents the result of a general-purpose provider health check.
/// Unlike <see cref="ProviderHealthResult"/> (which is LLM-specific and includes a <c>Model</c> field),
/// this type uses <see cref="DiagnosticType"/> and <see cref="Capability"/> fields appropriate for
/// non-LLM providers such as streaming services that have no "model" concept.
/// </summary>
/// <remarks>
/// <para>
/// Streaming plugins (Qobuzarr, Tidalarr, AppleMusicarr) should use this type instead of
/// <see cref="ProviderHealthResult"/> to avoid semantic confusion where <c>Model = "quality_detect"</c>
/// implies an LLM model identifier.
/// </para>
/// <para>
/// Use <see cref="FromLlmResult"/> and <see cref="ToLlmResult"/> to convert between this type
/// and <see cref="ProviderHealthResult"/> when interoperability is needed.
/// </para>
/// </remarks>
public record DiagnosticHealthResult
{
    /// <summary>
    /// Gets a value indicating whether the provider is healthy and ready.
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Gets an optional message describing the health status.
    /// </summary>
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Gets the time taken to perform the health check.
    /// </summary>
    public TimeSpan? ResponseTime { get; init; }

    /// <summary>
    /// Gets the name/identifier of the provider (e.g., 'tidal', 'qobuz', 'apple-music').
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Gets the authentication method used by the provider.
    /// </summary>
    /// <example>
    /// "oauth", "apiKey", "deviceCode", "token", "none"
    /// </example>
    public string? AuthMethod { get; init; }

    /// <summary>
    /// Gets the type of diagnostic check performed.
    /// Replaces the LLM-specific <c>Model</c> field with a semantically correct identifier
    /// for non-LLM providers.
    /// </summary>
    /// <example>
    /// "quality_detect", "connectivity", "auth_validate", "catalog_access", "stream_probe"
    /// </example>
    public string? DiagnosticType { get; init; }

    /// <summary>
    /// Gets the capability that was checked.
    /// Describes what the provider can do when healthy.
    /// </summary>
    /// <example>
    /// "hi_res_streaming", "lossless_download", "search", "metadata_lookup"
    /// </example>
    public string? Capability { get; init; }

    /// <summary>
    /// Gets the error code if the health check failed.
    /// </summary>
    /// <example>
    /// "AUTH_FAILED", "RATE_LIMITED", "CONNECTION_FAILED", "TIMEOUT", "REGION_BLOCKED"
    /// </example>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Creates a healthy result indicating the provider is ready.
    /// </summary>
    /// <param name="responseTime">Optional response time from the health check.</param>
    /// <param name="provider">Optional provider identifier.</param>
    /// <param name="authMethod">Optional authentication method.</param>
    /// <param name="diagnosticType">Optional diagnostic type (e.g., 'quality_detect').</param>
    /// <param name="capability">Optional capability identifier.</param>
    /// <returns>A healthy <see cref="DiagnosticHealthResult"/>.</returns>
    public static DiagnosticHealthResult Healthy(
        TimeSpan? responseTime = null,
        string? provider = null,
        string? authMethod = null,
        string? diagnosticType = null,
        string? capability = null) => new()
    {
        IsHealthy = true,
        StatusMessage = null,
        ResponseTime = responseTime,
        Provider = provider,
        AuthMethod = authMethod,
        DiagnosticType = diagnosticType,
        Capability = capability,
    };

    /// <summary>
    /// Creates an unhealthy result indicating the provider cannot accept requests.
    /// </summary>
    /// <param name="reason">The reason the provider is unhealthy.</param>
    /// <param name="responseTime">Optional response time from the health check.</param>
    /// <param name="provider">Optional provider identifier.</param>
    /// <param name="authMethod">Optional authentication method.</param>
    /// <param name="diagnosticType">Optional diagnostic type.</param>
    /// <param name="capability">Optional capability identifier.</param>
    /// <param name="errorCode">Optional error code for diagnostics.</param>
    /// <returns>An unhealthy <see cref="DiagnosticHealthResult"/>.</returns>
    public static DiagnosticHealthResult Unhealthy(
        string reason,
        TimeSpan? responseTime = null,
        string? provider = null,
        string? authMethod = null,
        string? diagnosticType = null,
        string? capability = null,
        string? errorCode = null) => new()
    {
        IsHealthy = false,
        StatusMessage = reason,
        ResponseTime = responseTime,
        Provider = provider,
        AuthMethod = authMethod,
        DiagnosticType = diagnosticType,
        Capability = capability,
        ErrorCode = errorCode,
    };

    /// <summary>
    /// Creates a degraded result indicating the provider is functional but impaired.
    /// </summary>
    /// <param name="reason">The reason the provider is degraded.</param>
    /// <param name="responseTime">Optional response time from the health check.</param>
    /// <param name="provider">Optional provider identifier.</param>
    /// <param name="authMethod">Optional authentication method.</param>
    /// <param name="diagnosticType">Optional diagnostic type.</param>
    /// <param name="capability">Optional capability identifier.</param>
    /// <returns>A degraded <see cref="DiagnosticHealthResult"/>.</returns>
    public static DiagnosticHealthResult Degraded(
        string reason,
        TimeSpan? responseTime = null,
        string? provider = null,
        string? authMethod = null,
        string? diagnosticType = null,
        string? capability = null) => new()
    {
        IsHealthy = true,
        StatusMessage = $"[Degraded] {reason}",
        ResponseTime = responseTime,
        Provider = provider,
        AuthMethod = authMethod,
        DiagnosticType = diagnosticType,
        Capability = capability,
    };

    /// <summary>
    /// Creates a <see cref="DiagnosticHealthResult"/> from an LLM-specific
    /// <see cref="ProviderHealthResult"/>. The <see cref="ProviderHealthResult.Model"/>
    /// field is mapped to <see cref="DiagnosticType"/>.
    /// </summary>
    /// <param name="llmResult">The LLM health result to convert.</param>
    /// <returns>An equivalent <see cref="DiagnosticHealthResult"/>.</returns>
    public static DiagnosticHealthResult FromLlmResult(ProviderHealthResult llmResult) => new()
    {
        IsHealthy = llmResult.IsHealthy,
        StatusMessage = llmResult.StatusMessage,
        ResponseTime = llmResult.ResponseTime,
        Provider = llmResult.Provider,
        AuthMethod = llmResult.AuthMethod,
        DiagnosticType = llmResult.Model,
        ErrorCode = llmResult.ErrorCode,
    };

    /// <summary>
    /// Converts this diagnostic result to an LLM-specific <see cref="ProviderHealthResult"/>.
    /// The <see cref="DiagnosticType"/> field is mapped to <see cref="ProviderHealthResult.Model"/>.
    /// </summary>
    /// <returns>An equivalent <see cref="ProviderHealthResult"/>.</returns>
    public ProviderHealthResult ToLlmResult() => new()
    {
        IsHealthy = IsHealthy,
        StatusMessage = StatusMessage,
        ResponseTime = ResponseTime,
        Provider = Provider,
        AuthMethod = AuthMethod,
        Model = DiagnosticType,
        ErrorCode = ErrorCode,
    };
}
