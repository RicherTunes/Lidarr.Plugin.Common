// <copyright file="LlmToolCall.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Text.Json;

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// A tool call the model emitted in its response. Surfaces on
/// <see cref="LlmResponse.ToolCalls"/> and is matched back to a follow-up
/// <see cref="LlmToolResult"/> via <see cref="Id"/>.
/// </summary>
/// <param name="Id">Vendor-supplied identifier for this call. OpenAI returns it as
/// <c>tool_calls[].id</c>; Anthropic uses the <c>tool_use</c> block <c>id</c>. Gemini does
/// not emit one natively, so the Gemini adapter synthesizes a stable id (e.g.
/// <c>"call_{index}"</c>) so that follow-up <see cref="LlmToolResult"/>s can be correlated
/// uniformly. The host MUST echo this id back on the corresponding result.</param>
/// <param name="Name">Name of the tool being invoked. Matches a previously-supplied
/// <see cref="LlmTool.Name"/>.</param>
/// <param name="Arguments">Parsed JSON arguments the model chose. Held as a
/// <see cref="JsonElement"/> so the host can access fields directly without an extra
/// re-parse.</param>
/// <param name="RawArguments">Optional pre-parse text exactly as the vendor returned it.
/// Useful for fidelity (e.g. preserving floating-point precision, key ordering, or original
/// JSON whitespace) and as a fallback when <see cref="Arguments"/> couldn't be parsed.</param>
public sealed record LlmToolCall(
    string Id,
    string Name,
    JsonElement Arguments,
    string? RawArguments = null);
