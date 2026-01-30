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
    /// Creates a healthy result indicating the provider is ready.
    /// </summary>
    /// <param name="responseTime">Optional response time from the health check.</param>
    /// <returns>A healthy <see cref="ProviderHealthResult"/>.</returns>
    public static ProviderHealthResult Healthy(TimeSpan? responseTime = null) => new()
    {
        IsHealthy = true,
        StatusMessage = null,
        ResponseTime = responseTime
    };

    /// <summary>
    /// Creates an unhealthy result indicating the provider cannot accept requests.
    /// </summary>
    /// <param name="reason">The reason the provider is unhealthy.</param>
    /// <param name="responseTime">Optional response time from the health check.</param>
    /// <returns>An unhealthy <see cref="ProviderHealthResult"/>.</returns>
    public static ProviderHealthResult Unhealthy(string reason, TimeSpan? responseTime = null) => new()
    {
        IsHealthy = false,
        StatusMessage = reason,
        ResponseTime = responseTime
    };

    /// <summary>
    /// Creates a degraded result indicating the provider is functional but impaired.
    /// Degraded providers can still accept requests but may have reduced performance.
    /// </summary>
    /// <param name="reason">The reason the provider is degraded.</param>
    /// <param name="responseTime">Optional response time from the health check.</param>
    /// <returns>A degraded <see cref="ProviderHealthResult"/> (marked as healthy but with warning message).</returns>
    public static ProviderHealthResult Degraded(string reason, TimeSpan? responseTime = null) => new()
    {
        IsHealthy = true,
        StatusMessage = $"[Degraded] {reason}",
        ResponseTime = responseTime
    };
}
