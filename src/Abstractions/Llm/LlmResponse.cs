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

    /// <summary>
    /// Gets the tool calls the model emitted in this response. Populated when the model chose
    /// to invoke one or more of the tools supplied via <see cref="LlmRequest.Tools"/>. Each entry
    /// carries an <see cref="LlmToolCall.Id"/> that the host echoes back on the corresponding
    /// follow-up <see cref="LlmToolResult"/>.
    /// </summary>
    /// <remarks>
    /// <para>Backward-compatible: defaults to <see langword="null"/>. Callers should check for
    /// <c>ToolCalls != null</c> regardless of <see cref="FinishReason"/> — different vendors set
    /// it differently (OpenAI: <c>"tool_calls"</c>; Anthropic: <c>"tool_use"</c>; Gemini may keep
    /// the OpenAI-style stop reason or omit it entirely) and the abstraction does not normalize
    /// the string.</para>
    /// <para>Source: brainarr Phase 5e P1 — closes the deferred tool-calling gap so that
    /// providers advertising <see cref="LlmCapabilityFlags.ToolCalling"/> have a shared data
    /// shape to surface model-emitted calls.</para>
    /// </remarks>
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }
}
