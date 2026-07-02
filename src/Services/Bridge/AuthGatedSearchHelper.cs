using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Bridge;

/// <summary>
/// Wraps a bridge SEARCH operation with the canonical auth-gating contract every streaming plugin
/// needs, so each plugin stops re-implementing (and diverging on) it:
/// <list type="number">
/// <item>short-circuit to an empty result when auth is latched bad (do not touch the network — the
/// qobuzarr IP-ban amplification fix);</item>
/// <item>run the search;</item>
/// <item>on an AUTHENTICATION failure (classified precisely by <see cref="AuthFailureClassifier"/>),
/// latch the gate and return an empty result so Lidarr's search loop degrades gracefully instead of
/// hammering a known-bad credential;</item>
/// <item>on ANY other failure — including the search executor's all-failed
/// <see cref="InvalidOperationException"/> — propagate unchanged, so the all-failed signal still
/// surfaces and is never mistaken for (and suppressed as) an auth failure.</item>
/// </list>
/// Genuine cancellation always propagates and never latches the gate.
/// </summary>
public static class AuthGatedSearchHelper
{
    public static async Task<IReadOnlyList<T>> ExecuteAsync<T>(
        AuthFailureGate gate,
        Func<CancellationToken, Task<IReadOnlyList<T>>> searchAsync,
        Func<Exception, int?>? statusOf = null,
        CancellationToken cancellationToken = default)
    {
        if (gate is null)
        {
            throw new ArgumentNullException(nameof(gate));
        }

        if (searchAsync is null)
        {
            throw new ArgumentNullException(nameof(searchAsync));
        }

        if (gate.ShouldShortCircuit())
        {
            return Array.Empty<T>();
        }

        try
        {
            return await searchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = AuthFailureClassifier.Classify(ex, statusOf);
            if (failure is null)
            {
                // Non-auth (incl. the executor all-failed signal and transient network/5xx): propagate
                // unchanged so the real failure surfaces and the gate is NOT wrongly latched.
                throw;
            }

            gate.RecordExceptionOutcome(ex, _ => failure);
            return Array.Empty<T>();
        }
    }
}
