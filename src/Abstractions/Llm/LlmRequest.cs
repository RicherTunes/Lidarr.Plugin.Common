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
    /// Gets optional provider-specific options for advanced configuration.
    /// These are passed through to the underlying provider without interpretation.
    /// Use for provider-specific features not covered by standard properties.
    /// </summary>
    public IReadOnlyDictionary<string, object>? ProviderOptions { get; init; }
}
