// <copyright file="LlmRequest.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Represents a request to an LLM provider for text completion.
/// This is a provider-agnostic data contract that works with any ILlmProvider implementation.
/// </summary>
public record LlmRequest
{
    /// <summary>
    /// Gets the user prompt to send to the LLM.
    /// This is the primary input text that the model will respond to.
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Gets the optional system prompt for configuring model behavior.
    /// System prompts typically set the persona, tone, or constraints for responses.
    /// Only honored when provider has <see cref="LlmCapabilityFlags.SystemPrompt"/> capability.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Gets the optional model identifier to use for this request.
    /// When null, the provider's default model is used.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets the optional sampling temperature for response generation.
    /// Typically ranges from 0.0 (deterministic) to 2.0 (creative).
    /// When null, the provider's default temperature is used.
    /// </summary>
    public float? Temperature { get; init; }

    /// <summary>
    /// Gets the optional maximum number of tokens to generate in the response.
    /// When null, the provider's default or maximum is used.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Gets the optional timeout for this specific request.
    /// Overrides any default timeout configured on the provider.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets a value indicating whether the provider should be instructed to return a
    /// strict JSON response (where supported). Each provider encodes this differently
    /// in its native protocol (for example OpenAI's <c>response_format=json_object</c>,
    /// Anthropic's prompt-conditioned JSON shaping, Gemini's <c>responseMimeType</c>,
    /// or Z.AI's OpenAI-compatible <c>response_format</c>) so the unified abstraction
    /// surfaces the intent here and lets each provider translate it.
    /// </summary>
    /// <remarks>
    /// Backward compatible: defaults to <c>false</c>. Providers that do not advertise
    /// the <see cref="LlmCapabilityFlags.JsonMode"/> capability should ignore this flag
    /// rather than fail.
    /// </remarks>
    public bool JsonMode { get; init; }

    /// <summary>
    /// Gets optional provider-specific options for advanced configuration.
    /// These are passed through to the underlying provider without interpretation.
    /// Use for provider-specific features not covered by standard properties.
    /// </summary>
    public IReadOnlyDictionary<string, object>? ProviderOptions { get; init; }

    /// <summary>
    /// Gets the optional thinking/reasoning hint for providers that support extended-thinking modes
    /// (Anthropic, Gemini, etc.). Defaults to <see langword="null"/>, in which case the adapter should
    /// not send an explicit thinking directive.
    /// </summary>
    /// <remarks>
    /// <para>Backward-compatible: when null, providers behave exactly as they did before this property
    /// existed. Providers that do not advertise <see cref="LlmCapabilityFlags.ExtendedThinking"/> should
    /// silently ignore the hint rather than fail.</para>
    /// <para>Source: brainarr Phase 4a feedback — adapters previously had to "sneak" thinking-mode through
    /// <see cref="Model"/> sentinels (e.g., <c>"claude-sonnet-thinking"</c>); this property lets them
    /// carry the intent explicitly.</para>
    /// </remarks>
    public LlmThinkingHint? Thinking { get; init; }

    /// <summary>
    /// Gets the optional set of tools the model is allowed to call on this request. When non-null and
    /// non-empty, providers that advertise <see cref="LlmCapabilityFlags.ToolCalling"/> translate this
    /// list into their native tool-declaration wire shape (OpenAI's <c>tools[].function</c>, Anthropic's
    /// <c>tools[]</c> with <c>input_schema</c>, Gemini's <c>functionDeclarations</c>, Z.AI's
    /// OpenAI-compatible shape). Defaults to <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>Backward-compatible: when null, providers behave exactly as they did before this property
    /// existed. Providers that do not advertise the <see cref="LlmCapabilityFlags.ToolCalling"/>
    /// capability should silently ignore tools rather than fail.</para>
    /// <para>Source: brainarr Phase 5e P1 — closes the deferred gap where the
    /// <see cref="LlmCapabilityFlags.ToolCalling"/> flag had no shared data shape, forcing tool-using
    /// consumers to bypass the adapter.</para>
    /// </remarks>
    public IReadOnlyList<LlmTool>? Tools { get; init; }

    /// <summary>
    /// Gets the tool-choice strategy that controls whether the model is allowed (or required) to call
    /// tools. Providers translate to the vendor's native shape (OpenAI's <c>tool_choice</c>, Anthropic's
    /// <c>tool_choice</c>, Gemini's <c>toolConfig.functionCallingConfig.mode</c>). Defaults to
    /// <see cref="LlmToolChoice.Auto"/>, which lets the model decide.
    /// </summary>
    /// <remarks>
    /// Backward-compatible: <see cref="LlmToolChoice.Auto"/> matches every vendor's pre-existing default
    /// behavior, so callers that do not set this field see no change.
    /// </remarks>
    public LlmToolChoice ToolChoice { get; init; } = LlmToolChoice.Auto;

    /// <summary>
    /// Gets prior tool outputs the host wants the model to see on this turn. Used in multi-turn agentic
    /// workflows: turn N emits <see cref="LlmResponse.ToolCalls"/>, the host runs the tools, and turn
    /// N+1 supplies their outputs here. Each <see cref="LlmToolResult.ToolCallId"/> matches a previously
    /// returned <see cref="LlmToolCall.Id"/>. Defaults to <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Backward-compatible: when null, providers behave exactly as they did before this property
    /// existed.
    /// </remarks>
    public IReadOnlyList<LlmToolResult>? ToolResults { get; init; }
}
