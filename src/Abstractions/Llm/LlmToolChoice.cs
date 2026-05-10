// <copyright file="LlmToolChoice.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Controls whether the model is allowed (or required) to call tools on a given request.
/// Carried on <see cref="LlmRequest.ToolChoice"/>; providers translate to their native
/// shape (OpenAI's <c>tool_choice</c>, Anthropic's <c>tool_choice</c>, Gemini's
/// <c>toolConfig.functionCallingConfig.mode</c>).
/// </summary>
public enum LlmToolChoice
{
    /// <summary>
    /// Default. The model decides whether to call a tool or respond directly. Maps to
    /// OpenAI <c>"auto"</c>, Anthropic <c>{"type":"auto"}</c>, Gemini <c>"AUTO"</c>.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Tool calls are disabled even when <see cref="LlmRequest.Tools"/> is non-empty.
    /// Maps to OpenAI <c>"none"</c>, Anthropic <c>{"type":"none"}</c>, Gemini <c>"NONE"</c>.
    /// </summary>
    None = 1,

    /// <summary>
    /// The model MUST call at least one tool from <see cref="LlmRequest.Tools"/>. Maps to
    /// OpenAI <c>"required"</c>, Anthropic <c>{"type":"any"}</c>, Gemini <c>"ANY"</c>.
    /// </summary>
    Required = 2,
}
