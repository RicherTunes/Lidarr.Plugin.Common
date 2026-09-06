using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Utilities;

/// <summary>
/// Cancellable retry waits with one bounded native timer at a time. Timer signals
/// are wake-ups, not proof that the requested monotonic duration has elapsed.
/// </summary>
internal static class ResilienceDelay
{
#if NET6_0_OR_GREATER
    private static readonly TimeSpan MaximumTimerDuration = TimeSpan.FromMilliseconds(uint.MaxValue - 1L);
#else
    private static readonly TimeSpan MaximumTimerDuration = TimeSpan.FromMilliseconds(int.MaxValue);
#endif

    public static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken
#if NET8_0_OR_GREATER
        , TimeProvider? timeProvider = null
#endif
        )
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A negative Retry-After means no wait, never an infinite timer.
        if (delay <= TimeSpan.Zero) return;

#if NET8_0_OR_GREATER
        var clock = timeProvider ?? TimeProvider.System;
        var wait = new ResilienceBudget(delay, clock);
#else
        var wait = new ResilienceBudget(delay);
#endif
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = wait.Remaining;
            if (remaining <= TimeSpan.Zero) return;

            // Round UP to the native millisecond quantum without adding to a
            // potentially maximal TimeSpan. A fractional delay must not become
            // a zero-duration hot loop; a long delay must not overflow the timer.
            var ticks = remaining > MaximumTimerDuration
                ? MaximumTimerDuration.Ticks
                : ((remaining.Ticks - 1) / TimeSpan.TicksPerMillisecond + 1) * TimeSpan.TicksPerMillisecond;
            var chunk = TimeSpan.FromTicks(ticks);
#if NET8_0_OR_GREATER
            await DelayChunkAsync(chunk, clock, cancellationToken).ConfigureAwait(false);
#else
            await Task.Delay(chunk, cancellationToken).ConfigureAwait(false);
#endif
        }
    }

#if NET8_0_OR_GREATER
    private static async Task DelayChunkAsync(TimeSpan chunk, TimeProvider clock, CancellationToken cancellationToken)
    {
        // Own cleanup across the await. Task.Delay may run a continuation inline
        // before its internal timer cleanup, allowing the next retry to overtake
        // that cleanup. Keep the previous shared helpers' explicit ownership.
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        using var timer = clock.CreateTimer(
            static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null),
            completion, chunk, Timeout.InfiniteTimeSpan);
        await completion.Task.ConfigureAwait(false);
    }
#endif
}
