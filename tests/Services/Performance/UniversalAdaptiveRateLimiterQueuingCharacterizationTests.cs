using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using Lidarr.Plugin.Common.Services.Performance;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Performance;

/// <summary>
/// Characterization of the limiter's slot-claim queuing (perf-program Phase 0;
/// see docs/superpowers/specs/2026-06-11-phase0-evidence.md).
/// Pins two facts the QoS-lane design depends on:
/// <list type="number">
///   <item>Within one endpoint bucket, a burst of waiters claims future slots
///   irrevocably, so a later arrival waits behind the whole burst — there is
///   no priority or reordering. This is the search-starvation mechanism:
///   tidal search and download metadata share the api.tidal.com:v1 bucket.</item>
///   <item>Different endpoint buckets do not contend (per-endpoint pacing).</item>
/// </list>
/// Bounds are generous one-sided limits: the slot math is exact and Task.Delay
/// only ever adds latency, so the lower bound cannot flake.
/// </summary>
public sealed class UniversalAdaptiveRateLimiterQueuingCharacterizationTests
{
    private const string Service = "Qobuz"; // default config: 500 RPM => 120 ms/slot
    private const double SlotMs = 60000.0 / 500;

    [Fact]
    public async Task SameBucket_BurstClaimsSlots_LaterArrivalWaitsBehindEntireBurst()
    {
        using var limiter = new UniversalAdaptiveRateLimiter();

        // 10 concurrent waiters claim slots t0, t0+120ms, ..., t0+1080ms.
        var burst = Enumerable.Range(0, 10)
            .Select(_ => limiter.WaitIfNeededAsync(Service, "albums"))
            .ToArray();
        await Task.Delay(100); // let every burst caller pass the claim point

        var sw = Stopwatch.StartNew();
        await limiter.WaitIfNeededAsync(Service, "albums");
        sw.Stop();
        await Task.WhenAll(burst);

        // The 11th arrival's slot is >= t0 + 10*120ms = t0+1200ms and ~100ms
        // have already elapsed. Generous bound: at least half the theoretical
        // ~1100ms remainder.
        Assert.True(sw.ElapsedMilliseconds >= 5 * SlotMs,
            $"Later same-bucket arrival should wait behind the burst; waited {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task DifferentBucket_BurstDoesNotDelayOtherEndpoint()
    {
        using var limiter = new UniversalAdaptiveRateLimiter();

        var burst = Enumerable.Range(0, 10)
            .Select(_ => limiter.WaitIfNeededAsync(Service, "albums"))
            .ToArray();
        await Task.Delay(100);

        var sw = Stopwatch.StartNew();
        await limiter.WaitIfNeededAsync(Service, "search"); // different bucket
        sw.Stop();
        await Task.WhenAll(burst);

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Different bucket must not queue behind the burst; waited {sw.ElapsedMilliseconds} ms");
    }
}
