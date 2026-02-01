// <copyright file="LlmUsage.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Represents token usage information for an LLM request/response.
/// Used for monitoring, cost estimation, and context window management.
/// </summary>
public record LlmUsage
{
    /// <summary>
    /// Gets the number of tokens in the input prompt.
    /// Includes both the system prompt and user prompt if applicable.
    /// </summary>
    public int InputTokens { get; init; }

    /// <summary>
    /// Gets the number of tokens generated in the response.
    /// </summary>
    public int OutputTokens { get; init; }

    /// <summary>
    /// Gets the total number of tokens used (input + output).
    /// Useful for context window tracking and cost estimation.
    /// </summary>
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// Gets the estimated cost in USD for this request, if calculable.
    /// Based on the provider's pricing model and token counts.
    /// Null if pricing information is not available.
    /// </summary>
    public decimal? EstimatedCostUsd { get; init; }
}
