// <copyright file="LlmToolResult.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// The host's reply to a previously-emitted <see cref="LlmToolCall"/>. Carried on
/// <see cref="LlmRequest.ToolResults"/> for the follow-up turn so the model can incorporate
/// the tool's output. Providers translate to the vendor's tool-result shape (OpenAI's
/// <c>role=tool</c> message with <c>tool_call_id</c>, Anthropic's
/// <c>content[].type=tool_result</c> block, Gemini's <c>functionResponse</c> part).
/// </summary>
/// <param name="ToolCallId">Identifier of the originating <see cref="LlmToolCall.Id"/>.
/// Required so the model can match this result to the call it emitted.</param>
/// <param name="Output">Serialized tool output the model should see. Typically JSON, but
/// may be a plain string — providers pass this through verbatim. Callers are responsible
/// for any size-limiting or sanitization (the abstraction does not impose a max length).</param>
/// <param name="IsError">When <see langword="true"/>, signals the tool failed and the model
/// should treat <see cref="Output"/> as an error description rather than a successful
/// result. Maps to OpenAI's lack of explicit flag (errors stringified into output),
/// Anthropic's <c>tool_result.is_error</c>, and Gemini's error-shaped <c>functionResponse</c>.</param>
public sealed record LlmToolResult(
    string ToolCallId,
    string Output,
    bool IsError = false);
