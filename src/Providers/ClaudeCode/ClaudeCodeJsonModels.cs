// <copyright file="ClaudeCodeJsonModels.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace Lidarr.Plugin.Common.Providers.ClaudeCode;

/// <summary>
/// Represents the JSON response from Claude Code CLI when using --output-format json.
/// Based on Go SDK type definitions: github.com/yukifoo/claude-code-sdk-go.
/// </summary>
public record ClaudeCliResponse
{
    /// <summary>
    /// Gets the type of the response message.
    /// Common values: "result", "error".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>
    /// Gets the generated text content from the model.
    /// Contains the actual response text for successful completions.
    /// Contains error message when <see cref="IsError"/> is true.
    /// </summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary>
    /// Gets the unique session identifier for this CLI invocation.
    /// Can be used to correlate logs and track conversation context.
    /// </summary>
    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = "";

    /// <summary>
    /// Gets a value indicating whether the CLI encountered an error.
    /// When true, <see cref="Result"/> contains the error message.
    /// </summary>
    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

    /// <summary>
    /// Gets the number of conversation turns in this session.
    /// For single-turn completions (--max-turns 1), this is typically 1.
    /// </summary>
    [JsonPropertyName("num_turns")]
    public int NumTurns { get; init; }

    /// <summary>
    /// Gets the total execution duration in milliseconds.
    /// Includes network latency, model inference, and CLI overhead.
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; init; }

    /// <summary>
    /// Gets the total cost in USD for this request.
    /// Null if cost information is not available.
    /// </summary>
    [JsonPropertyName("total_cost_usd")]
    public decimal? TotalCostUsd { get; init; }

    /// <summary>
    /// Gets the token usage statistics for this request.
    /// Null if usage information is not available.
    /// </summary>
    [JsonPropertyName("usage")]
    public ClaudeCliUsage? Usage { get; init; }
}

/// <summary>
/// Represents token usage statistics from Claude Code CLI.
/// </summary>
public record ClaudeCliUsage
{
    /// <summary>
    /// Gets the number of tokens in the input prompt.
    /// Includes system prompt, user prompt, and any context.
    /// </summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    /// <summary>
    /// Gets the number of tokens in the generated output.
    /// </summary>
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }
}
