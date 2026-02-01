// <copyright file="LlmStreamChunk.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Represents a single chunk in a streaming LLM response.
/// Returned by <see cref="ILlmProvider.StreamAsync"/> for providers
/// that support the <see cref="LlmCapabilityFlags.Streaming"/> capability.
/// </summary>
public record LlmStreamChunk
{
    /// <summary>
    /// Gets the incremental content text in this chunk.
    /// Concatenate all ContentDelta values to build the complete response.
    /// May be null for metadata-only chunks (e.g., final usage report).
    /// </summary>
    public string? ContentDelta { get; init; }

    /// <summary>
    /// Gets the incremental reasoning text for models that support extended thinking.
    /// Populated when provider has <see cref="LlmCapabilityFlags.ExtendedThinking"/> capability
    /// and is streaming reasoning output.
    /// </summary>
    public string? ReasoningDelta { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is the final chunk in the stream.
    /// When true, the stream is complete and no more chunks will follow.
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>
    /// Gets the final token usage information, populated only on the last chunk.
    /// Check <see cref="IsComplete"/> is true before accessing this value.
    /// </summary>
    public LlmUsage? FinalUsage { get; init; }
}
