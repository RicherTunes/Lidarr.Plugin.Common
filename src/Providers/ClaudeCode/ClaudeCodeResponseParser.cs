// <copyright file="ClaudeCodeResponseParser.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Errors;

namespace Lidarr.Plugin.Common.Providers.ClaudeCode;

/// <summary>
/// Parses Claude Code CLI JSON output into <see cref="LlmResponse"/> objects.
/// </summary>
public static class ClaudeCodeResponseParser
{
    private const string ProviderId = "claude-code";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parses JSON output from Claude Code CLI into an <see cref="LlmResponse"/>.
    /// </summary>
    /// <param name="json">The JSON string from CLI stdout.</param>
    /// <returns>A parsed <see cref="LlmResponse"/> with content, usage, and metadata.</returns>
    /// <exception cref="ProviderException">
    /// Thrown when JSON is null/empty, malformed, or indicates an error.
    /// </exception>
    public static LlmResponse ParseJsonResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ProviderException(
                ProviderId,
                LlmErrorCode.InvalidRequest,
                "CLI returned empty response");
        }

        ClaudeCliResponse? cliResponse;
        try
        {
            cliResponse = JsonSerializer.Deserialize<ClaudeCliResponse>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ProviderException(
                ProviderId,
                LlmErrorCode.InvalidRequest,
                $"Failed to parse CLI JSON response: {ex.Message}",
                ex);
        }

        if (cliResponse == null)
        {
            throw new ProviderException(
                ProviderId,
                LlmErrorCode.InvalidRequest,
                "CLI returned null response after deserialization");
        }

        if (cliResponse.IsError)
        {
            throw new ProviderException(
                ProviderId,
                LlmErrorCode.InvalidRequest,
                cliResponse.Result ?? "Unknown CLI error");
        }

        return new LlmResponse
        {
            Content = cliResponse.Result ?? "",
            Usage = MapUsage(cliResponse),
            FinishReason = "stop",
            Metadata = BuildMetadata(cliResponse),
        };
    }

    private static LlmUsage? MapUsage(ClaudeCliResponse response)
    {
        if (response.Usage == null)
        {
            return null;
        }

        return new LlmUsage
        {
            InputTokens = response.Usage.InputTokens,
            OutputTokens = response.Usage.OutputTokens,
            EstimatedCostUsd = response.TotalCostUsd,
        };
    }

    private static IReadOnlyDictionary<string, object> BuildMetadata(ClaudeCliResponse response)
    {
        var metadata = new Dictionary<string, object>
        {
            ["session_id"] = response.SessionId,
            ["num_turns"] = response.NumTurns,
            ["duration_ms"] = response.DurationMs,
        };

        return new ReadOnlyDictionary<string, object>(metadata);
    }
}
