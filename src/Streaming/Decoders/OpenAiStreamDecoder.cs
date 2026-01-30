// <copyright file="OpenAiStreamDecoder.cs" company="RicherTunes">
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
/// Decodes OpenAI-format SSE streaming responses.
/// Compatible with OpenAI API and compatible endpoints (Azure OpenAI, local models, etc.).
/// </summary>
/// <remarks>
/// <para>
/// OpenAI streaming format:
/// - Content-Type: text/event-stream
/// - Data: JSON with choices[].delta.content
/// - Terminator: data: [DONE]
/// </para>
/// </remarks>
public sealed class OpenAiStreamDecoder : IStreamDecoder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] DefaultSupportedProviders =
        ["openai", "azure-openai", "openrouter", "together", "anyscale", "fireworks"];

    /// <inheritdoc />
    public string DecoderId => "openai";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProviderIds { get; } = DefaultSupportedProviders;

    /// <inheritdoc />
    public bool CanDecode(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        // OpenAI uses text/event-stream for streaming
        return contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool CanDecodeForProvider(string providerId, string contentType)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        // Check if provider is in our supported list and content type is SSE
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
        string? finishReason = null;
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

            OpenAiStreamResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<OpenAiStreamResponse>(frame.Data, JsonOptions);
            }
            catch (JsonException)
            {
                // Skip malformed JSON chunks
                continue;
            }

            if (response?.Choices == null || response.Choices.Count == 0)
            {
                // Check for usage-only chunk (some APIs send this at the end)
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
            finishReason = choice.FinishReason ?? finishReason;

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

    // OpenAI streaming response DTOs
    private sealed class OpenAiStreamResponse
    {
        public string? Id { get; set; }
        public string? Object { get; set; }
        public long Created { get; set; }
        public string? Model { get; set; }
        public List<OpenAiChoice>? Choices { get; set; }
        public OpenAiUsage? Usage { get; set; }
    }

    private sealed class OpenAiChoice
    {
        public int Index { get; set; }
        public OpenAiDelta? Delta { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class OpenAiDelta
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }

    private sealed class OpenAiUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}
