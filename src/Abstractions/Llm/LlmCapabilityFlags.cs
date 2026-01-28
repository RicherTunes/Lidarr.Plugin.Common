// <copyright file="LlmCapabilityFlags.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Flags indicating the capabilities supported by an LLM provider.
/// These flags enable capability detection for intelligent feature usage.
/// </summary>
[Flags]
public enum LlmCapabilityFlags
{
    /// <summary>
    /// No capabilities. Default value.
    /// </summary>
    None = 0,

    /// <summary>
    /// Provider supports basic text completion (prompt in, text out).
    /// This is the most fundamental capability that all providers should support.
    /// </summary>
    TextCompletion = 1 << 0,

    /// <summary>
    /// Provider supports streaming responses via Server-Sent Events or similar.
    /// When set, <see cref="ILlmProvider.StreamAsync"/> returns a valid async enumerable.
    /// </summary>
    Streaming = 1 << 1,

    /// <summary>
    /// Provider supports JSON mode for structured output.
    /// When set, the provider can be instructed to output valid JSON.
    /// </summary>
    JsonMode = 1 << 2,

    /// <summary>
    /// Provider supports system prompts for behavior configuration.
    /// When set, <see cref="LlmRequest.SystemPrompt"/> is honored.
    /// </summary>
    SystemPrompt = 1 << 3,

    /// <summary>
    /// Provider supports function/tool calling capabilities.
    /// Enables agentic workflows with tool use.
    /// </summary>
    ToolCalling = 1 << 4,

    /// <summary>
    /// Provider supports vision/image input.
    /// Enables multimodal prompts with images.
    /// </summary>
    Vision = 1 << 5,

    /// <summary>
    /// Provider supports extended thinking or reasoning modes.
    /// Examples: Claude's thinking mode, GLM's reasoning capabilities.
    /// When set, <see cref="LlmResponse.ReasoningContent"/> may be populated.
    /// </summary>
    ExtendedThinking = 1 << 6,

    /// <summary>
    /// Provider can accurately count or estimate tokens.
    /// Enables pre-request token estimation for context management.
    /// </summary>
    TokenCounting = 1 << 7
}
