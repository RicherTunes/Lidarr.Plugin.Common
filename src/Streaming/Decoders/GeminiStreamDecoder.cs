// <copyright file="GeminiStreamDecoder.cs" company="RicherTunes">
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
/// Decodes Google Gemini-format SSE streaming responses.
/// </summary>
/// <remarks>
/// <para>
/// Gemini streaming format:
/// - Content-Type: text/event-stream
/// - Data: JSON with candidates[].content.parts[].text
/// - Terminator: finishReason in candidates (no [DONE] signal)
/// - Usage: usageMetadata at message end
/// </para>
/// </remarks>
public sealed class GeminiStreamDecoder : IStreamDecoder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] DefaultSupportedProviders = ["gemini", "google"];

    /// <inheritdoc />
    public string DecoderId => "gemini";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProviderIds { get; } = DefaultSupportedProviders;

    /// <inheritdoc />
    public bool CanDecode(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        // Gemini uses text/event-stream for streaming
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
            if (string.IsNullOrWhiteSpace(frame.Data))
            {
                continue;
            }

            GeminiStreamResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<GeminiStreamResponse>(frame.Data, JsonOptions);
            }
            catch (JsonException)
            {
                // Skip malformed JSON chunks
                continue;
            }

            // Check for usage metadata (may come in separate chunk or with content)
            if (response?.UsageMetadata != null)
            {
                usage = new LlmUsage
                {
                    InputTokens = response.UsageMetadata.PromptTokenCount,
                    OutputTokens = response.UsageMetadata.CandidatesTokenCount,
                };
            }

            if (response?.Candidates == null || response.Candidates.Count == 0)
            {
                // Usage-only chunk at the end - check if stream ends here
                continue;
            }

            var candidate = response.Candidates[0];
            var content = ExtractTextFromParts(candidate.Content?.Parts);
            var finishReason = candidate.FinishReason;

            // Check for finish reason indicating end
            if (!string.IsNullOrEmpty(finishReason))
            {
                // Yield final chunk with any remaining content
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

        // Stream ended without finish reason - yield completion
        yield return new LlmStreamChunk
        {
            IsComplete = true,
            FinalUsage = usage,
        };
    }

    private static string? ExtractTextFromParts(List<GeminiPart>? parts)
    {
        if (parts == null || parts.Count == 0)
        {
            return null;
        }

        // Concatenate all text parts (usually just one)
        var textParts = parts
            .Where(p => !string.IsNullOrEmpty(p.Text))
            .Select(p => p.Text);

        var combined = string.Join(string.Empty, textParts);
        return string.IsNullOrEmpty(combined) ? null : combined;
    }

    // Gemini streaming response DTOs
    private sealed class GeminiStreamResponse
    {
        public List<GeminiCandidate>? Candidates { get; set; }
        public GeminiUsageMetadata? UsageMetadata { get; set; }
    }

    private sealed class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }
        public int Index { get; set; }
        public string? FinishReason { get; set; }
    }

    private sealed class GeminiContent
    {
        public List<GeminiPart>? Parts { get; set; }
        public string? Role { get; set; }
    }

    private sealed class GeminiPart
    {
        public string? Text { get; set; }
    }

    private sealed class GeminiUsageMetadata
    {
        public int PromptTokenCount { get; set; }
        public int CandidatesTokenCount { get; set; }
        public int TotalTokenCount { get; set; }
    }
}
