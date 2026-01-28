// <copyright file="ILlmProvider.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Core abstraction for LLM providers. Focused on raw LLM operations,
/// not domain-specific tasks like recommendation generation.
/// Inspired by Microsoft.Extensions.AI patterns.
/// </summary>
/// <remarks>
/// <para>
/// This interface provides a unified way to interact with various LLM providers
/// (OpenAI, Anthropic, Google, local models, etc.) through a common contract.
/// </para>
/// <para>
/// Implementations should handle:
/// - Authentication and API key management
/// - Rate limiting and retry logic
/// - Error mapping to normalized exceptions
/// - Logging with proper redaction of sensitive data
/// </para>
/// <para>
/// For domain-specific operations (e.g., music recommendations), create adapter
/// classes that use ILlmProvider internally but expose domain-specific methods.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Basic usage
/// var provider = GetProvider(); // ILlmProvider
/// var response = await provider.CompleteAsync(new LlmRequest
/// {
///     Prompt = "Hello, world!",
///     Temperature = 0.7f
/// });
/// Console.WriteLine(response.Content);
///
/// // Streaming usage
/// if (provider.Capabilities.Flags.HasFlag(LlmCapabilityFlags.Streaming))
/// {
///     var stream = provider.StreamAsync(request);
///     if (stream != null)
///     {
///         await foreach (var chunk in stream)
///         {
///             Console.Write(chunk.ContentDelta);
///         }
///     }
/// }
/// </code>
/// </example>
public interface ILlmProvider
{
    /// <summary>
    /// Gets the unique identifier for this provider.
    /// Used for logging, configuration, and provider selection.
    /// </summary>
    /// <example>
    /// Common values: "openai", "anthropic", "google", "glm", "ollama", "claude-code"
    /// </example>
    string ProviderId { get; }

    /// <summary>
    /// Gets the human-readable display name for this provider.
    /// Suitable for display in user interfaces.
    /// </summary>
    /// <example>
    /// Common values: "OpenAI GPT", "Claude", "Gemini", "Z.AI GLM", "Ollama"
    /// </example>
    string DisplayName { get; }

    /// <summary>
    /// Gets the capabilities this provider supports.
    /// Use this to check for features before attempting to use them.
    /// </summary>
    /// <seealso cref="LlmCapabilityFlags"/>
    /// <seealso cref="LlmProviderCapabilities"/>
    LlmProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Checks if the provider is ready to accept requests.
    /// Verifies authentication, connectivity, and service availability.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the health check.</param>
    /// <returns>
    /// A <see cref="ProviderHealthResult"/> indicating whether the provider is healthy.
    /// </returns>
    /// <remarks>
    /// Implementations should:
    /// - Verify API key/credentials are valid
    /// - Check if the service endpoint is reachable
    /// - Return quickly (typically under 5 seconds)
    /// - Not throw exceptions; return unhealthy result instead
    /// </remarks>
    Task<ProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a prompt and returns the full response (non-streaming).
    /// </summary>
    /// <param name="request">The request containing the prompt and configuration.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>
    /// The complete response from the LLM.
    /// </returns>
    /// <exception cref="LlmProviderException">
    /// Thrown when the request fails due to authentication, rate limiting, or provider errors.
    /// (See Lidarr.Plugin.Common.Errors namespace for concrete exception types.)
    /// </exception>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a prompt completion, returning chunks as they arrive.
    /// </summary>
    /// <param name="request">The request containing the prompt and configuration.</param>
    /// <param name="cancellationToken">Token to cancel the stream.</param>
    /// <returns>
    /// An async enumerable of response chunks, or <c>null</c> if streaming is not supported.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Returns <c>null</c> (not an empty enumerable) if the provider does not support streaming.
    /// Check <see cref="Capabilities"/>.<see cref="LlmProviderCapabilities.Flags"/> for
    /// <see cref="LlmCapabilityFlags.Streaming"/> before calling this method.
    /// </para>
    /// <para>
    /// The return type is nullable <see cref="IAsyncEnumerable{T}"/> rather than
    /// <see cref="Task{T}"/> because providers that don't support streaming can return
    /// null synchronously without awaiting.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// if (provider.Capabilities.Flags.HasFlag(LlmCapabilityFlags.Streaming))
    /// {
    ///     var stream = provider.StreamAsync(request);
    ///     if (stream != null)
    ///     {
    ///         await foreach (var chunk in stream)
    ///         {
    ///             if (chunk.ContentDelta != null)
    ///                 Console.Write(chunk.ContentDelta);
    ///             if (chunk.IsComplete)
    ///                 Console.WriteLine($"\n[Done: {chunk.FinalUsage?.TotalTokens} tokens]");
    ///         }
    ///     }
    /// }
    /// </code>
    /// </example>
    IAsyncEnumerable<LlmStreamChunk>? StreamAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
