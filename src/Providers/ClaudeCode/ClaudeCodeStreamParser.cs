// <copyright file="ClaudeCodeStreamParser.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Subprocess;

namespace Lidarr.Plugin.Common.Providers.ClaudeCode;

/// <summary>
/// Parses NDJSON streaming output from Claude Code CLI when using --output-format stream-json.
/// </summary>
/// <remarks>
/// <para>
/// Claude Code CLI stream format emits newline-delimited JSON objects.
/// Each line is a complete JSON object representing a streaming event.
/// </para>
/// <para>
/// Event types include:
/// - message_start: Initial message metadata
/// - content_block_start: Beginning of a content block
/// - content_block_delta: Incremental content (text, thinking)
/// - content_block_stop: End of a content block
/// - message_delta: Final message metadata with stop_reason
/// - message_stop: End of message
/// - result: Final result with usage stats
/// </para>
/// </remarks>
public sealed class ClaudeCodeStreamParser
{
    // Known event types that we handle or intentionally ignore
    private static readonly HashSet<string> KnownEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "message_start",
        "content_block_start",
        "content_block_delta",
        "content_block_stop",
        "message_delta",
        "message_stop",
        "result",
        "error",
        "ping",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Action<string>? _unknownEventLogger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeCodeStreamParser"/> class.
    /// </summary>
    /// <param name="unknownEventLogger">Optional callback to log unknown event types (tolerant mode).</param>
    public ClaudeCodeStreamParser(Action<string>? unknownEventLogger = null)
    {
        _unknownEventLogger = unknownEventLogger;
    }

    /// <summary>
    /// Parses CLI stream events into LLM chunks.
    /// </summary>
    /// <param name="events">The CLI stream events (stdout lines).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of LLM stream chunks.</returns>
    public async IAsyncEnumerable<LlmStreamChunk> ParseAsync(
        IAsyncEnumerable<CliStreamEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LlmUsage? finalUsage = null;
        var hasYieldedCompletion = false;

        await foreach (var evt in events.ConfigureAwait(false))
        {
            if (evt is not CliStreamEvent.StandardOutput output)
            {
                // Handle process exit
                if (evt is CliStreamEvent.Exited exited)
                {
                    // Yield completion chunk if not already done
                    if (!hasYieldedCompletion)
                    {
                        yield return new LlmStreamChunk
                        {
                            IsComplete = true,
                            FinalUsage = finalUsage,
                        };
                        hasYieldedCompletion = true;
                    }
                }

                continue;
            }

            var line = output.Text?.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            // Parse the JSON event
            ClaudeStreamEvent? streamEvent;
            try
            {
                streamEvent = JsonSerializer.Deserialize<ClaudeStreamEvent>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // Skip malformed JSON lines
                continue;
            }

            if (streamEvent == null)
            {
                continue;
            }

            // Process based on event type
            switch (streamEvent.Type)
            {
                case "content_block_delta":
                    var delta = streamEvent.Delta;
                    if (delta != null)
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
                        }
                    }

                    break;

                case "message_delta":
                    // Contains stop_reason and usage
                    if (streamEvent.Usage != null)
                    {
                        finalUsage = new LlmUsage
                        {
                            InputTokens = streamEvent.Usage.InputTokens,
                            OutputTokens = streamEvent.Usage.OutputTokens,
                        };
                    }

                    break;

                case "message_stop":
                case "result":
                    // Final event - extract usage if available
                    if (streamEvent.Usage != null)
                    {
                        finalUsage = new LlmUsage
                        {
                            InputTokens = streamEvent.Usage.InputTokens,
                            OutputTokens = streamEvent.Usage.OutputTokens,
                        };
                    }

                    // Check for result type which includes final stats
                    if (streamEvent.Type == "result" && streamEvent.Result != null)
                    {
                        // Result contains the final aggregated response
                        // Usage should already be captured above
                    }

                    yield return new LlmStreamChunk
                    {
                        IsComplete = true,
                        FinalUsage = finalUsage,
                    };
                    hasYieldedCompletion = true;
                    break;

                case "error":
                    // Error event - could throw or yield error info
                    var errorMessage = streamEvent.Error?.Message ?? "Unknown streaming error";
                    throw new InvalidOperationException($"Claude Code streaming error: {errorMessage}");

                // Structural event types - intentionally ignored
                case "message_start":
                case "content_block_start":
                case "content_block_stop":
                case "ping":
                    // These are structural/keepalive events that don't contain content
                    break;

                default:
                    // Unknown event type - log if callback provided (tolerant mode)
                    if (streamEvent.Type != null && !KnownEventTypes.Contains(streamEvent.Type))
                    {
                        _unknownEventLogger?.Invoke(
                            $"Unknown Claude stream event type: '{streamEvent.Type}'. " +
                            "This may indicate a CLI update - please report if this causes issues.");
                    }

                    break;
            }
        }

        // Ensure we yield completion if stream ends without explicit message_stop
        if (!hasYieldedCompletion)
        {
            yield return new LlmStreamChunk
            {
                IsComplete = true,
                FinalUsage = finalUsage,
            };
        }
    }

    // Claude Code streaming event DTOs
    private sealed class ClaudeStreamEvent
    {
        public string? Type { get; set; }
        public ClaudeDelta? Delta { get; set; }
        public ClaudeUsage? Usage { get; set; }
        public ClaudeResult? Result { get; set; }
        public ClaudeError? Error { get; set; }
        public int? Index { get; set; }
    }

    private sealed class ClaudeDelta
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
        public string? Thinking { get; set; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }
    }

    private sealed class ClaudeUsage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }
    }

    private sealed class ClaudeResult
    {
        public string? Type { get; set; }
        public string? Result { get; set; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("is_error")]
        public bool IsError { get; set; }

        [JsonPropertyName("num_turns")]
        public int NumTurns { get; set; }

        [JsonPropertyName("duration_ms")]
        public int DurationMs { get; set; }

        [JsonPropertyName("total_cost_usd")]
        public decimal? TotalCostUsd { get; set; }
    }

    private sealed class ClaudeError
    {
        public string? Type { get; set; }
        public string? Message { get; set; }
    }
}
