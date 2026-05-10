// <copyright file="LlmThinkingHint.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

namespace Lidarr.Plugin.Common.Abstractions.Llm;

/// <summary>
/// Selects how a provider should treat extended-thinking / reasoning hints attached to an
/// <see cref="LlmRequest.Thinking"/>.
/// </summary>
/// <remarks>
/// <para>Source: brainarr Phase 4a feedback ("Anthropic and Gemini both accept thinking/reasoning hints; a
/// common <c>LlmRequest.Thinking { Mode, BudgetTokens }</c> record would let the adapter carry them
/// explicitly instead of sneaking them through <c>request.Model</c> sentinels").</para>
/// </remarks>
public enum LlmThinkingMode
{
    /// <summary>
    /// Do not request thinking. The provider should suppress reasoning when possible (Anthropic
    /// <c>thinking.type=disabled</c>, Gemini <c>thinkingBudget=0</c>, OpenAI default).
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Let the provider decide. The adapter should not send an explicit thinking directive — the
    /// provider's default behavior applies.
    /// </summary>
    Auto = 1,

    /// <summary>
    /// Request that the provider perform extended thinking. <see cref="LlmThinkingHint.BudgetTokens"/>,
    /// when set, hints at a thinking-token budget where supported (Anthropic <c>thinking.budget_tokens</c>,
    /// Gemini <c>thinkingConfig.thinkingBudget</c>).
    /// </summary>
    Enabled = 2,
}

/// <summary>
/// Optional thinking/reasoning hint carried on an <see cref="LlmRequest"/>. Providers translate the hint
/// into their native protocol (Anthropic's <c>thinking</c>, Gemini's <c>thinkingConfig</c>, etc.) and
/// silently ignore it when the underlying model does not advertise
/// <see cref="LlmCapabilityFlags.ExtendedThinking"/>.
/// </summary>
/// <param name="Mode">How the provider should treat thinking on this request.</param>
/// <param name="BudgetTokens">
/// Optional thinking-token budget. Hint only — providers that don't support a budget knob ignore this
/// value. Negative values are treated as <see langword="null"/>.
/// </param>
public sealed record LlmThinkingHint(LlmThinkingMode Mode, int? BudgetTokens = null);
