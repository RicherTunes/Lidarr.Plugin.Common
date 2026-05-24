using System;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// R3-1 regression: with failureThreshold &gt; 1, failures 1..K-1 used to
/// leave the gate in IsHealthy=true state, bypassing rate-limiting and
/// re-enabling the IP-ban scenario. The fix is that the gate must apply
/// probe-interval rate-limiting as SOON as a single failure has been
/// observed, not only after status flips to Failed.
/// </summary>
public sealed class AuthFailureGateSubThresholdTests
{
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public async Task Threshold3_FirstFailure_RateLimitsProbeSlot()
    {
        // The exact R3-1 reproduction: threshold=3, only one failure has
        // occurred, so status is still Unknown — but the gate MUST already
        // be rate-limiting probe slots because one failure means SOMETHING
        // is wrong upstream. Without this, the plugin pounds upstream K-1
        // times at full rate.
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero));
        var handler = new DefaultAuthFailureHandler(
            NullLogger<DefaultAuthFailureHandler>.Instance,
            failureThreshold: 3);
        var gate = new AuthFailureGate(handler, clock, TimeSpan.FromSeconds(60),
            NullLogger<AuthFailureGate>.Instance);

        await handler.HandleFailureAsync(new AuthFailure { Message = "first flake" });

        // First probe attempt consumes the slot.
        Assert.True(gate.TryAcquireProbeSlot());

        // Second attempt inside the probe interval must be rate-limited
        // even though status is still Unknown (sub-threshold).
        Assert.False(gate.TryAcquireProbeSlot(),
            "sub-threshold failures must still trigger probe-interval rate-limiting");
    }

    [Fact]
    public void Threshold3_NoFailuresYet_DoesNotRateLimit()
    {
        // Sanity: when zero failures have been observed, the gate is fully
        // open with no rate-limiting (initial healthy state).
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero));
        var handler = new DefaultAuthFailureHandler(
            NullLogger<DefaultAuthFailureHandler>.Instance,
            failureThreshold: 3);
        var gate = new AuthFailureGate(handler, clock, TimeSpan.FromSeconds(60),
            NullLogger<AuthFailureGate>.Instance);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(gate.TryAcquireProbeSlot());
        }
    }

    [Fact]
    public async Task Threshold3_FailureThenSuccess_ClearsRateLimit()
    {
        // Once an intervening success resets the streak, the gate must be
        // fully open again — the failure was transient.
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero));
        var handler = new DefaultAuthFailureHandler(
            NullLogger<DefaultAuthFailureHandler>.Instance,
            failureThreshold: 3);
        var gate = new AuthFailureGate(handler, clock, TimeSpan.FromSeconds(60),
            NullLogger<AuthFailureGate>.Instance);

        await handler.HandleFailureAsync(new AuthFailure { Message = "flake" });
        Assert.True(gate.TryAcquireProbeSlot());
        Assert.False(gate.TryAcquireProbeSlot()); // rate-limited

        await handler.HandleSuccessAsync(); // streak broken

        // After the success, the gate is fully open again — multiple
        // probes allowed without rate-limiting.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(gate.TryAcquireProbeSlot());
        }
    }
}
