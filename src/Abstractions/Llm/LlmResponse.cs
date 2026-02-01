// <copyright file="LlmResponse.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Represents a response from an LLM provider after text completion.
/// This is a provider-agnostic data contract returned by any ILlmProvider implementation.
/// </summary>
public record LlmResponse
{
    /// <summary>
    /// Gets the generated text content from the model.
    /// This is the primary output of the completion request.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Gets the reasoning or thinking content for models that support extended thinking.
    /// Populated when provider has <see cref="LlmCapabilityFlags.ExtendedThinking"/> capability
    /// and the model provides separate reasoning output (e.g., Claude's thinking, GLM reasoning).
    /// </summary>
    public string? ReasoningContent { get; init; }

    /// <summary>
    /// Gets the token usage information for this request/response pair.
    /// May be null if the provider doesn't report usage or the capability is not supported.
    /// </summary>
    public LlmUsage? Usage { get; init; }

    /// <summary>
    /// Gets the reason why the model stopped generating text.
    /// Common values: "stop" (natural completion), "length" (max tokens reached),
    /// "content_filter" (safety filter triggered).
    /// </summary>
    public string? FinishReason { get; init; }

    /// <summary>
    /// Gets optional provider-specific metadata about the response.
    /// Contains additional information that varies by provider.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
