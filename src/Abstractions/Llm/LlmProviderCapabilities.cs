// <copyright file="LlmProviderCapabilities.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Describes the capabilities and constraints of an LLM provider.
/// Used for capability detection and intelligent provider selection.
/// </summary>
public record LlmProviderCapabilities
{
    /// <summary>
    /// Gets the capability flags indicating which features this provider supports.
    /// Use bitwise operations to check for specific capabilities.
    /// </summary>
    /// <example>
    /// <code>
    /// if (capabilities.Flags.HasFlag(LlmCapabilityFlags.Streaming))
    /// {
    ///     // Provider supports streaming
    /// }
    /// </code>
    /// </example>
    public required LlmCapabilityFlags Flags { get; init; }

    /// <summary>
    /// Gets the maximum context window size in tokens, if known.
    /// Null indicates the limit is unknown or unlimited.
    /// </summary>
    public int? MaxContextTokens { get; init; }

    /// <summary>
    /// Gets the maximum output tokens the provider can generate in a single response.
    /// Null indicates the limit is unknown or unlimited.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Gets the list of model identifiers supported by this provider.
    /// Empty list indicates the provider accepts any model string or has a single default model.
    /// </summary>
    public IReadOnlyList<string> SupportedModels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets a value indicating whether this provider uses an OpenAI-compatible API.
    /// When true, the provider can potentially be used with OpenAI-compatible clients.
    /// </summary>
    public bool UsesOpenAiCompatibleApi { get; init; }
}
