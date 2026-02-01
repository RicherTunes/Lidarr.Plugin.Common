// <copyright file="ZaiStreamDecoder.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Lidarr.Plugin.Common.Abstractions.Llm;

namespace Lidarr.Plugin.Common.Streaming.Decoders;

/// <summary>
/// Decodes Z.AI GLM-format SSE streaming responses.
/// </summary>
/// <remarks>
/// <para>
/// Z.AI GLM uses an OpenAI-compatible streaming format:
/// - Content-Type: text/event-stream
/// - Data: JSON with choices[].delta.content (OpenAI format)
/// - Terminator: data: [DONE]
/// - Usage: usage object in final chunks
/// </para>
/// <para>
/// This decoder handles Z.AI-specific extensions like web_search results
/// while delegating standard parsing to OpenAI-compatible logic.
/// </para>
/// </remarks>
public sealed class ZaiStreamDecoder : IStreamDecoder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] DefaultSupportedProviders = ["zai", "glm", "zhipu"];

    /// <inheritdoc />
    public string DecoderId => "zai";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProviderIds { get; } = DefaultSupportedProviders;

    /// <inheritdoc />
    public bool CanDecode(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        // Z.AI uses text/event-stream for streaming
        return contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool CanDecodeForProvider(string providerId, string contentType)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var isProviderSupported = SupportedProviderIds.Any(
            p => p.Equals(providerId, StringComparison.OrdinalIgnoreCase));

        return isProviderSupported && CanDecode(contentType);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LlmStreamChunk> DecodeAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sseReader = new SseFramingReader(stream);
        LlmUsage? usage = null;

        await foreach (var frame in sseReader.ReadFramesAsync(cancellationToken))
        {
            if (frame.IsDone)
            {
                // Final frame - yield completion chunk
                yield return new LlmStreamChunk
                {
                    IsComplete = true,
                    FinalUsage = usage,
                };
                yield break;
            }

            if (string.IsNullOrWhiteSpace(frame.Data))
            {
                continue;
            }

            ZaiStreamResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<ZaiStreamResponse>(frame.Data, JsonOptions);
            }
            catch (JsonException)
            {
                // Skip malformed JSON chunks
                continue;
            }

            // Handle Z.AI-specific web_search results (log or extract if needed)
            // For now, we just pass through to content extraction

            if (response?.Choices == null || response.Choices.Count == 0)
            {
                // Check for usage-only chunk
                if (response?.Usage != null)
                {
                    usage = new LlmUsage
                    {
                        InputTokens = response.Usage.PromptTokens,
                        OutputTokens = response.Usage.CompletionTokens,
                    };
                }

                continue;
            }

            var choice = response.Choices[0];
            var content = choice.Delta?.Content;

            // Check for finish_reason indicating end
            if (choice.FinishReason != null)
            {
                // Capture any final usage from this chunk
                if (response.Usage != null)
                {
                    usage = new LlmUsage
                    {
                        InputTokens = response.Usage.PromptTokens,
                        OutputTokens = response.Usage.CompletionTokens,
                    };
                }

                // Yield any remaining content plus completion marker
                yield return new LlmStreamChunk
                {
                    ContentDelta = string.IsNullOrEmpty(content) ? null : content,
                    IsComplete = true,
                    FinalUsage = usage,
                };
                yield break;
            }

            // Normal content chunk
            if (!string.IsNullOrEmpty(content))
            {
                yield return new LlmStreamChunk
                {
                    ContentDelta = content,
                    IsComplete = false,
                };
            }
        }

        // Stream ended without [DONE] or finish_reason - yield completion
        yield return new LlmStreamChunk
        {
            IsComplete = true,
            FinalUsage = usage,
        };
    }

    // Z.AI GLM streaming response DTOs (OpenAI-compatible with extensions)
    private sealed class ZaiStreamResponse
    {
        public string? Id { get; set; }
        public string? Object { get; set; }
        public long Created { get; set; }
        public string? Model { get; set; }
        public List<ZaiChoice>? Choices { get; set; }
        public ZaiUsage? Usage { get; set; }

        // Z.AI-specific extensions
        public ZaiWebSearch? WebSearch { get; set; }
    }

    private sealed class ZaiChoice
    {
        public int Index { get; set; }
        public ZaiDelta? Delta { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class ZaiDelta
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }

    private sealed class ZaiUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }

    /// <summary>
    /// Z.AI-specific web search results (for future use).
    /// </summary>
    private sealed class ZaiWebSearch
    {
        public List<ZaiSearchResult>? SearchResults { get; set; }
    }

    private sealed class ZaiSearchResult
    {
        public string? Title { get; set; }
        public string? Link { get; set; }
        public string? Content { get; set; }
    }
}
