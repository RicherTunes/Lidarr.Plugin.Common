// <copyright file="StreamingException.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;

namespace Lidarr.Plugin.Common.Streaming;

/// <summary>
/// Base exception for streaming-related errors.
/// </summary>
public class StreamingException : Exception
{
    /// <summary>
    /// Gets the decoder ID that encountered the error, if applicable.
    /// </summary>
    public string? DecoderId { get; }

    /// <summary>
    /// Gets the provider ID that was being streamed from, if applicable.
    /// </summary>
    public string? ProviderId { get; }

    /// <summary>
    /// Gets the streaming diagnostics captured at the time of failure.
    /// </summary>
    public StreamingDiagnostics? Diagnostics { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingException"/> class.
    /// </summary>
    public StreamingException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingException"/> class.
    /// </summary>
    public StreamingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingException"/> class.
    /// </summary>
    public StreamingException(string message, string? decoderId, string? providerId)
        : base(message)
    {
        DecoderId = decoderId;
        ProviderId = providerId;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingException"/> class.
    /// </summary>
    public StreamingException(string message, string? decoderId, string? providerId, Exception innerException)
        : base(message, innerException)
    {
        DecoderId = decoderId;
        ProviderId = providerId;
    }
}

/// <summary>
/// Exception thrown when an SSE event exceeds the maximum allowed size.
/// </summary>
public sealed class StreamFrameTooLargeException : StreamingException
{
    /// <summary>
    /// Gets the maximum allowed event size in bytes.
    /// </summary>
    public int MaxEventSize { get; }

    /// <summary>
    /// Gets the actual event size in bytes (approximate).
    /// </summary>
    public int ActualSize { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamFrameTooLargeException"/> class.
    /// </summary>
    public StreamFrameTooLargeException(int maxEventSize, int actualSize)
        : base($"SSE event exceeds maximum allowed size of {maxEventSize:N0} bytes (received ~{actualSize:N0} bytes). " +
               "Configure a larger maxEventSize if this is expected.")
    {
        MaxEventSize = maxEventSize;
        ActualSize = actualSize;
    }
}

/// <summary>
/// Exception thrown when a streaming operation times out.
/// </summary>
public sealed class StreamTimeoutException : StreamingException
{
    /// <summary>
    /// Gets the cancellation reason that caused the timeout.
    /// </summary>
    public StreamingCancellationReason TimeoutReason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamTimeoutException"/> class.
    /// </summary>
    public StreamTimeoutException(StreamingCancellationReason reason, TimeSpan timeout)
        : base(GetMessage(reason, timeout))
    {
        TimeoutReason = reason;
    }

    private static string GetMessage(StreamingCancellationReason reason, TimeSpan timeout)
    {
        return reason switch
        {
            StreamingCancellationReason.FirstChunkTimeout =>
                $"No data received within first-chunk timeout ({timeout.TotalSeconds:F1}s)",
            StreamingCancellationReason.InterChunkTimeout =>
                $"Stream stalled: no data received within inter-chunk timeout ({timeout.TotalSeconds:F1}s)",
            StreamingCancellationReason.TotalStreamTimeout =>
                $"Stream exceeded total timeout ({timeout.TotalMinutes:F1} minutes)",
            _ => $"Stream timeout: {reason}",
        };
    }
}
