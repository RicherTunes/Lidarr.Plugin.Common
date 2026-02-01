// <copyright file="ClaudeCodeSettings.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;

namespace Lidarr.Plugin.Common.Providers.ClaudeCode;

/// <summary>
/// Configuration settings for Claude Code CLI provider.
/// </summary>
public sealed record ClaudeCodeSettings
{
    /// <summary>
    /// Default timeout for CLI operations (2 minutes).
    /// </summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Graceful shutdown timeout when cancelling operations.
    /// </summary>
    public TimeSpan GracefulShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Model alias to use (sonnet, opus, haiku). Default is sonnet.
    /// </summary>
    public string Model { get; init; } = "sonnet";

    /// <summary>
    /// Health check timeout (shorter than default).
    /// </summary>
    public TimeSpan HealthCheckTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
