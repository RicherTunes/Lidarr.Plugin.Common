// <copyright file="AnthropicStreamDecoder.cs" company="RicherTunes">
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
/// Decodes Anthropic-format SSE streaming responses (Messages API).
/// </summary>
/// <remarks>
/// <para>
/// Anthropic streaming wire format:
/// </para>
/// <list type="bullet">
/// <item><description>Content-Type: <c>text/event-stream</c></description></item>
/// <item><description>Each event has both an <c>event:</c> name and a JSON <c>data:</c> payload.</description></item>
/// <item><description>Event sequence: <c>message_start</c>, then zero-or-more <c>content_block_*</c> events,
/// then <c>message_delta</c> (carries final usage), then <c>message_stop</c>.</description></item>
/// <item><description>Text deltas arrive in <c>content_block_delta</c> events whose <c>delta.type</c> is
/// <c>text_delta</c>. Reasoning deltas (extended thinking) use <c>thinking_delta</c> and surface as
/// <see cref="LlmStreamChunk.ReasoningDelta"/>.</description></item>
/// <item><description>There is no <c>[DONE]</c> sentinel; <c>message_stop</c> (or end of stream) terminates.</description></item>
/// </list>
/// <para>
/// Usage: <c>message_start.message.usage</c> usually carries the input token count;
/// <c>message_delta.usage</c> typically carries the final output_tokens. Both are folded
/// into a single <see cref="LlmUsage"/> on the terminal chunk.
/// </para>
/// </remarks>
public sealed class AnthropicStreamDecoder : IStreamDecoder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] DefaultSupportedProviders = ["anthropic", "claude"];

    /// <inheritdoc />
    public string DecoderId => "anthropic";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProviderIds { get; } = DefaultSupportedProviders;

    /// <inheritdoc />
    public bool CanDecode(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        return contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool CanDecodeForProvider(string providerId, string contentType)
    {
        if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var supported = SupportedProviderIds.Any(
            p => p.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        return supported && CanDecode(contentType);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LlmStreamChunk> DecodeAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sseReader = new SseFramingReader(stream);
        int? inputTokens = null;
        int? outputTokens = null;
        bool yieldedTerminal = false;

        await foreach (var frame in sseReader.ReadFramesAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(frame.Data))
            {
                continue;
            }

            // Event type comes from the SSE event field; payload is JSON in data.
            var eventType = frame.EventType ?? string.Empty;

            // Anthropic sometimes emits an explicit error event or the connection
            // may close mid-stream; we let JSON exceptions skip individual frames.
            AnthropicEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<AnthropicEnvelope>(frame.Data, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (envelope == null)
            {
                continue;
            }

            // Some servers omit the "event:" field and rely on the "type" property in the JSON payload.
            // Prefer the explicit event field when present, else fall back to envelope.Type.
            var effective = string.IsNullOrEmpty(eventType) ? envelope.Type : eventType;

            switch (effective)
            {
                case "message_start":
                    if (envelope.Message?.Usage is { } startUsage)
                    {
                        inputTokens = startUsage.InputTokens ?? inputTokens;
                        outputTokens = startUsage.OutputTokens ?? outputTokens;
                    }

                    break;

                case "content_block_delta":
                    if (envelope.Delta is { } delta)
                    {
                        switch (delta.Type)
                        {
                            case "text_delta":
                                if (!string.IsNullOrEmpty(delta.Text))
                                {
                                    yield return new LlmStreamChunk
                                    {
                                        ContentDelta = delta.Text,
                                        IsComplete = false,
                                    };
                                }

                                break;

                            case "thinking_delta":
                                if (!string.IsNullOrEmpty(delta.Thinking))
                                {
                                    yield return new LlmStreamChunk
                                    {
                                        ReasoningDelta = delta.Thinking,
                                        IsComplete = false,
                                    };
                                }

                                break;

                            // Other delta types (input_json_delta, etc.) are ignored for now.
                        }
                    }

                    break;

                case "message_delta":
                    if (envelope.Usage is { } finalUsage)
                    {
                        // message_delta typically updates output_tokens (and may overwrite input_tokens).
                        if (finalUsage.InputTokens.HasValue)
                        {
                            inputTokens = finalUsage.InputTokens;
                        }

                        if (finalUsage.OutputTokens.HasValue)
                        {
                            outputTokens = finalUsage.OutputTokens;
                        }
                    }

                    break;

                case "message_stop":
                    yield return new LlmStreamChunk
                    {
                        IsComplete = true,
                        FinalUsage = BuildUsage(inputTokens, outputTokens),
                    };
                    yieldedTerminal = true;
                    yield break;

                // ping / content_block_start / content_block_stop / error: ignored
            }
        }

        // Stream ended without an explicit message_stop event - emit a terminal chunk so callers
        // do not have to special-case half-open streams.
        if (!yieldedTerminal)
        {
            yield return new LlmStreamChunk
            {
                IsComplete = true,
                FinalUsage = BuildUsage(inputTokens, outputTokens),
            };
        }
    }

    private static LlmUsage? BuildUsage(int? inputTokens, int? outputTokens)
    {
        if (!inputTokens.HasValue && !outputTokens.HasValue)
        {
            return null;
        }

        return new LlmUsage
        {
            InputTokens = inputTokens ?? 0,
            OutputTokens = outputTokens ?? 0,
        };
    }

    // Anthropic streaming response DTOs (single envelope shape covering all event types)
    private sealed class AnthropicEnvelope
    {
        public string? Type { get; set; }

        // message_start
        public AnthropicMessage? Message { get; set; }

        // content_block_delta
        public AnthropicDelta? Delta { get; set; }

        // message_delta
        public AnthropicUsage? Usage { get; set; }

        // content_block_start / content_block_delta / content_block_stop
        [JsonPropertyName("index")]
        public int? Index { get; set; }
    }

    private sealed class AnthropicMessage
    {
        public string? Id { get; set; }

        public AnthropicUsage? Usage { get; set; }
    }

    private sealed class AnthropicDelta
    {
        public string? Type { get; set; }

        public string? Text { get; set; }

        public string? Thinking { get; set; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }
    }

    private sealed class AnthropicUsage
    {
        [JsonPropertyName("input_tokens")]
        public int? InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int? OutputTokens { get; set; }
    }
}
