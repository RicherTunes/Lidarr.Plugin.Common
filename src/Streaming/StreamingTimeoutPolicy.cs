// <copyright file="StreamingTimeoutPolicy.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;

namespace Lidarr.Plugin.Common.Streaming;

/// <summary>
/// Configures timeout behavior for streaming operations.
/// Provides granular control over first-chunk, inter-chunk, and total stream timeouts.
/// </summary>
public sealed record StreamingTimeoutPolicy
{
    /// <summary>
    /// Gets the default timeout policy with conservative defaults.
    /// </summary>
    public static StreamingTimeoutPolicy Default { get; } = new()
    {
        FirstChunkTimeout = TimeSpan.FromSeconds(30),
        InterChunkTimeout = TimeSpan.FromSeconds(15),
        TotalStreamTimeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Gets the maximum time to wait for the first chunk of data.
    /// If no data arrives within this timeout, the stream fails.
    /// </summary>
    /// <remarks>
    /// This timeout accounts for:
    /// - Network latency to the provider
    /// - Provider initialization time
    /// - Model loading (for local providers)
    /// - Queue wait time (for rate-limited APIs)
    /// </remarks>
    public TimeSpan FirstChunkTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the maximum time to wait between consecutive chunks.
    /// If no data arrives within this timeout after receiving a chunk, the stream is considered stalled.
    /// </summary>
    /// <remarks>
    /// This timeout detects:
    /// - Stalled connections
    /// - Provider hangs
    /// - Network interruptions mid-stream
    /// </remarks>
    public TimeSpan InterChunkTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets the maximum total duration for the entire stream.
    /// The stream will be cancelled if this timeout is exceeded, regardless of activity.
    /// </summary>
    /// <remarks>
    /// This prevents unbounded resource consumption from very long streams.
    /// Set to <see cref="TimeSpan.MaxValue"/> to disable the total timeout
    /// (not recommended for production).
    /// </remarks>
    public TimeSpan TotalStreamTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates a timeout policy optimized for fast local providers (e.g., Ollama).
    /// </summary>
    /// <returns>A policy with shorter timeouts suitable for local inference.</returns>
    public static StreamingTimeoutPolicy ForLocalProvider() => new()
    {
        FirstChunkTimeout = TimeSpan.FromSeconds(10),
        InterChunkTimeout = TimeSpan.FromSeconds(5),
        TotalStreamTimeout = TimeSpan.FromMinutes(3),
    };

    /// <summary>
    /// Creates a timeout policy optimized for cloud providers with potential latency.
    /// </summary>
    /// <returns>A policy with longer timeouts for cloud-based LLMs.</returns>
    public static StreamingTimeoutPolicy ForCloudProvider() => new()
    {
        FirstChunkTimeout = TimeSpan.FromSeconds(60),
        InterChunkTimeout = TimeSpan.FromSeconds(30),
        TotalStreamTimeout = TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// Creates a timeout policy for the Claude Code CLI provider.
    /// </summary>
    /// <returns>A policy tuned for CLI-based streaming.</returns>
    public static StreamingTimeoutPolicy ForClaudeCode() => new()
    {
        FirstChunkTimeout = TimeSpan.FromSeconds(45),
        InterChunkTimeout = TimeSpan.FromSeconds(20),
        TotalStreamTimeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Validates that the timeout values are sensible.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if any timeout is negative or zero.
    /// </exception>
    public void Validate()
    {
        if (FirstChunkTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FirstChunkTimeout),
                FirstChunkTimeout,
                "First chunk timeout must be positive.");
        }

        if (InterChunkTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InterChunkTimeout),
                InterChunkTimeout,
                "Inter-chunk timeout must be positive.");
        }

        if (TotalStreamTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TotalStreamTimeout),
                TotalStreamTimeout,
                "Total stream timeout must be positive.");
        }
    }
}
