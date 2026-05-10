// <copyright file="LlmTool.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Text.Json;

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Vendor-neutral description of a tool the model is permitted to call. Carried on
/// <see cref="LlmRequest.Tools"/>; each provider translates this into its native wire
/// format (OpenAI's <c>tools[].function</c>, Anthropic's <c>tools[]</c> with
/// <c>input_schema</c>, Gemini's <c>functionDeclarations</c>, Z.AI's OpenAI-compatible
/// shape, etc.).
/// </summary>
/// <param name="Name">Identifier the model uses to invoke the tool. Must match across
/// <see cref="LlmTool"/>, <see cref="LlmToolCall"/>, and <see cref="LlmToolResult"/>.</param>
/// <param name="Description">Human-readable description of what the tool does. Steers when the
/// model chooses to call it. Per spec this slot is non-optional — pass <see cref="string.Empty"/>
/// when no description is available rather than null.</param>
/// <param name="ParametersSchema">JSON Schema describing the tool's input parameters. Held as a
/// <see cref="JsonElement"/> so the schema can be carried through the pipeline without lossy
/// re-serialization. Providers serialize it directly into the vendor's parameter slot
/// (<c>parameters</c> for OpenAI/Gemini, <c>input_schema</c> for Anthropic).</param>
/// <param name="Kind">Tool kind. Defaults to <see cref="LlmToolKind.Function"/> — the only
/// universally-supported shape today; future kinds (web search, code interpreter, etc.) can
/// extend the enum without breaking the contract.</param>
/// <remarks>
/// <para>Source: brainarr Phase 5e P1 — providers needed a shared tool-calling shape so the
/// <see cref="LlmCapabilityFlags.ToolCalling"/> flag (already exposed) could carry data through
/// the abstraction instead of forcing consumers to bypass it.</para>
/// </remarks>
public sealed record LlmTool(
    string Name,
    string Description,
    JsonElement ParametersSchema,
    LlmToolKind Kind = LlmToolKind.Function);
