// <copyright file="StreamingDiagnostics.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;

namespace Lidarr.Plugin.Common.Streaming;

/// <summary>
/// Captures diagnostic information about a streaming operation.
/// Useful for debugging E2E failures and performance analysis.
/// </summary>
public sealed record StreamingDiagnostics
{
    /// <summary>
    /// Gets the decoder ID that processed the stream.
    /// </summary>
    public string? DecoderId { get; init; }

    /// <summary>
    /// Gets the provider ID that generated the stream.
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>
    /// Gets the reason the stream was cancelled, if applicable.
    /// </summary>
    public StreamingCancellationReason? CancellationReason { get; init; }

    /// <summary>
    /// Gets the total elapsed time for the streaming operation.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Gets the time to receive the first chunk.
    /// </summary>
    public TimeSpan? TimeToFirstChunk { get; init; }

    /// <summary>
    /// Gets the total number of chunks received.
    /// </summary>
    public int ChunkCount { get; init; }

    /// <summary>
    /// Gets the total number of bytes received.
    /// </summary>
    public long BytesReceived { get; init; }

    /// <summary>
    /// Gets a value indicating whether the stream completed successfully.
    /// </summary>
    public bool CompletedSuccessfully { get; init; }

    /// <summary>
    /// Gets the error message if the stream failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a diagnostics instance for a successful completion.
    /// </summary>
    public static StreamingDiagnostics Success(
        string? decoderId,
        string? providerId,
        TimeSpan elapsed,
        TimeSpan? timeToFirstChunk,
        int chunkCount,
        long bytesReceived) => new()
    {
        DecoderId = decoderId,
        ProviderId = providerId,
        Elapsed = elapsed,
        TimeToFirstChunk = timeToFirstChunk,
        ChunkCount = chunkCount,
        BytesReceived = bytesReceived,
        CompletedSuccessfully = true,
    };

    /// <summary>
    /// Creates a diagnostics instance for a cancelled stream.
    /// </summary>
    public static StreamingDiagnostics Cancelled(
        string? decoderId,
        string? providerId,
        StreamingCancellationReason reason,
        TimeSpan elapsed,
        int chunkCount) => new()
    {
        DecoderId = decoderId,
        ProviderId = providerId,
        CancellationReason = reason,
        Elapsed = elapsed,
        ChunkCount = chunkCount,
        CompletedSuccessfully = false,
        ErrorMessage = reason switch
        {
            StreamingCancellationReason.FirstChunkTimeout => "No data received within first-chunk timeout",
            StreamingCancellationReason.InterChunkTimeout => "Stream stalled: no data received within inter-chunk timeout",
            StreamingCancellationReason.TotalStreamTimeout => "Stream exceeded total timeout",
            StreamingCancellationReason.ExternalCancellation => "Stream cancelled by caller",
            _ => "Stream cancelled",
        },
    };

    /// <summary>
    /// Creates a diagnostics instance for a failed stream.
    /// </summary>
    public static StreamingDiagnostics Failed(
        string? decoderId,
        string? providerId,
        TimeSpan elapsed,
        int chunkCount,
        string errorMessage) => new()
    {
        DecoderId = decoderId,
        ProviderId = providerId,
        Elapsed = elapsed,
        ChunkCount = chunkCount,
        CompletedSuccessfully = false,
        ErrorMessage = errorMessage,
    };
}
