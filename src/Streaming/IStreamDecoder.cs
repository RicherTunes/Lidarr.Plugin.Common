// <copyright file="IStreamDecoder.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.IO;
using System.Threading;
using Lidarr.Plugin.Common.Abstractions.Llm;

namespace Lidarr.Plugin.Common.Streaming;

/// <summary>
/// Decodes streaming responses from LLM providers into a unified chunk format.
/// Each provider has its own streaming format (SSE payloads, NDJSON, etc.),
/// and decoders translate these to <see cref="LlmStreamChunk"/>.
/// </summary>
/// <remarks>
/// <para>
/// The decoder architecture separates:
/// 1. SSE framing (handled by <see cref="SseFramingReader"/>)
/// 2. Payload parsing (handled by specific decoders)
/// </para>
/// <para>
/// For non-SSE streams (like NDJSON), decoders read directly from the stream.
/// </para>
/// </remarks>
public interface IStreamDecoder
{
    /// <summary>
    /// Gets the unique identifier for this decoder.
    /// </summary>
    /// <example>
    /// Common values: "openai", "anthropic", "gemini", "zai", "claude-code"
    /// </example>
    string DecoderId { get; }

    /// <summary>
    /// Gets the provider IDs this decoder is designed for.
    /// Used for explicit decoder selection when content type alone is ambiguous.
    /// </summary>
    /// <example>
    /// OpenAI decoder might support: ["openai", "azure-openai", "openrouter"]
    /// </example>
    IReadOnlyList<string> SupportedProviderIds { get; }

    /// <summary>
    /// Determines if this decoder can handle the given content type.
    /// </summary>
    /// <param name="contentType">The Content-Type header value from the HTTP response.</param>
    /// <returns>True if this decoder can process the content type.</returns>
    /// <remarks>
    /// Multiple decoders may return true for the same content type (e.g., text/event-stream).
    /// Use <see cref="CanDecodeForProvider"/> for unambiguous selection.
    /// </remarks>
    bool CanDecode(string contentType);

    /// <summary>
    /// Determines if this decoder can handle the given provider and content type.
    /// This is the preferred selection method when provider ID is known.
    /// </summary>
    /// <param name="providerId">The provider ID generating the stream.</param>
    /// <param name="contentType">The Content-Type header value from the HTTP response.</param>
    /// <returns>True if this decoder can process the stream.</returns>
    bool CanDecodeForProvider(string providerId, string contentType);

    /// <summary>
    /// Decodes the stream into LLM chunks asynchronously.
    /// </summary>
    /// <param name="stream">The response stream to decode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of decoded chunks.</returns>
    /// <remarks>
    /// <para>
    /// Implementations should:
    /// - Yield chunks as soon as they're available (no buffering)
    /// - Handle provider-specific error formats
    /// - Set <see cref="LlmStreamChunk.IsComplete"/> on the final chunk
    /// - Include <see cref="LlmStreamChunk.FinalUsage"/> on the last chunk if available
    /// </para>
    /// <para>
    /// Cancellation should be checked frequently to avoid blocking during cleanup.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<LlmStreamChunk> DecodeAsync(
        Stream stream,
        CancellationToken cancellationToken = default);
}
