// <copyright file="LlmToolKind.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Discriminator for the kind of tool an <see cref="LlmTool"/> represents. Today only
/// <see cref="Function"/> is universally supported across vendor providers (OpenAI, Anthropic,
/// Gemini, Z.AI/GLM); the enum exists so future tool kinds (vendor-hosted web search, code
/// interpreter, file search, etc.) can be added without breaking the abstraction.
/// </summary>
public enum LlmToolKind
{
    /// <summary>
    /// A free-form function the host implements. The model chooses arguments matching the
    /// tool's <see cref="LlmTool.ParametersSchema"/>; the host runs the function and returns
    /// the result via <see cref="LlmToolResult"/>.
    /// </summary>
    Function = 0,
}
