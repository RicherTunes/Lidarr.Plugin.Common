using System;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// R2-11: when this fires in production at 3am, the operator needs to know
/// whether "no recommendations" is gated auth, network, or quota. Today the
/// gate emits one LogWarning per failure (drowned out by the upstream's own
/// errors). Counters at latch / probe / refund / recovery transitions give
/// the operator something they can graph.
/// </summary>
public sealed class AuthFailureGateMetricsTests
{
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static (AuthFailureGate Gate, DefaultAuthFailureHandler Handler, FakeClock Clock) New()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero));
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);
        return (new AuthFailureGate(handler, clock, TimeSpan.FromSeconds(60), NullLogger<AuthFailureGate>.Instance),
                handler, clock);
    }

    [Fact]
    public void Metrics_InitialState_AllZero()
    {
        var (gate, _, _) = New();
        var m = gate.Metrics;
        Assert.Equal(0, m.LatchTransitions);
        Assert.Equal(0, m.ProbeAcquired);
        Assert.Equal(0, m.ProbeRejected);
        Assert.Equal(0, m.ProbeRefunded);
        Assert.Equal(0, m.RecoveryTransitions);
        Assert.Null(m.LastLatchAt);
        Assert.Null(m.LastRecoveryAt);
    }

    [Fact]
    public async Task Metrics_OnLatch_RecordsTransitionAndTime()
    {
        var (gate, handler, clock) = New();
        await handler.HandleFailureAsync(new AuthFailure { Message = "fail" });

        // Trigger ResetIfRecovered observation so the gate notices the
        // transition via TryAcquireProbeSlot.
        gate.TryAcquireProbeSlot();

        var m = gate.Metrics;
        Assert.Equal(1, m.LatchTransitions);
        Assert.Equal(clock.GetUtcNow(), m.LastLatchAt);
    }

    [Fact]
    public async Task Metrics_ProbeAcquiredAndRejected_Counted()
    {
        var (gate, handler, _) = New();
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });

        Assert.True(gate.TryAcquireProbeSlot());   // +1 acquired
        Assert.False(gate.TryAcquireProbeSlot());  // +1 rejected
        Assert.False(gate.TryAcquireProbeSlot());  // +1 rejected

        var m = gate.Metrics;
        Assert.Equal(1, m.ProbeAcquired);
        Assert.Equal(2, m.ProbeRejected);
    }

    [Fact]
    public async Task Metrics_ProbeRefunded_Counted()
    {
        var (gate, handler, _) = New();
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });

        var ts = gate.AcquireProbeSlotWithTimestamp();
        Assert.NotNull(ts);
        gate.RefundProbeSlot(ts!.Value);

        Assert.Equal(1, gate.Metrics.ProbeRefunded);
    }

    [Fact]
    public async Task Metrics_OnRecovery_RecordsTransitionAndTime()
    {
        var (gate, handler, clock) = New();
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });
        gate.TryAcquireProbeSlot(); // observe latch

        clock.Advance(TimeSpan.FromSeconds(30));
        await handler.HandleSuccessAsync();
        gate.TryAcquireProbeSlot(); // observe recovery transition

        var m = gate.Metrics;
        Assert.Equal(1, m.RecoveryTransitions);
        Assert.Equal(clock.GetUtcNow(), m.LastRecoveryAt);
    }
}
