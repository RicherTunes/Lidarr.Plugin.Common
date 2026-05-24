using System;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Tests for <see cref="AuthFailureGate"/>: a fail-fast latch that prevents
/// plugins from hammering an upstream service when authentication is known
/// to be bad. Real-world driver: a user got IP-banned by Qobuz because
/// Lidarr kept searching, each plugin call returned 401, and the plugin
/// just propagated the error — Lidarr retried at full search rate.
/// </summary>
public sealed class AuthFailureGateTests
{
    private static AuthFailureGate NewGate(out FakeClock clock, TimeSpan? probeInterval = null)
    {
        clock = new FakeClock(new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero));
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);
        return new AuthFailureGate(handler, clock, probeInterval ?? TimeSpan.FromSeconds(60),
            NullLogger<AuthFailureGate>.Instance);
    }

    [Fact]
    public void EnsureCanProceed_WhenStatusUnknown_DoesNotThrow()
    {
        var gate = NewGate(out _);
        gate.EnsureCanProceed(); // initial Unknown state must not block
    }

    [Fact]
    public async Task EnsureCanProceed_AfterSuccess_DoesNotThrow()
    {
        var gate = NewGate(out _);
        await gate.Handler.HandleSuccessAsync();

        gate.EnsureCanProceed();
    }

    [Fact]
    public async Task EnsureCanProceed_AfterFailure_ThrowsAuthGatedException()
    {
        var gate = NewGate(out _);
        await gate.Handler.HandleFailureAsync(new AuthFailure { ErrorCode = "401", Message = "bad token" });

        var ex = Assert.Throws<AuthGatedException>(() => gate.EnsureCanProceed());
        Assert.Equal("401", ex.ErrorCode);
        Assert.Contains("bad token", ex.Message);
    }

    [Fact]
    public async Task EnsureCanProceed_AfterRequestReauthentication_ThrowsAuthGatedException()
    {
        var gate = NewGate(out _);
        await gate.Handler.RequestReauthenticationAsync("token expired");

        Assert.Throws<AuthGatedException>(() => gate.EnsureCanProceed());
    }

    [Fact]
    public async Task EnsureCanProceed_AfterFailureThenSuccess_DoesNotThrow()
    {
        var gate = NewGate(out _);
        await gate.Handler.HandleFailureAsync(new AuthFailure { Message = "fail" });
        await gate.Handler.HandleSuccessAsync(); // user re-credentialed; latch clears

        gate.EnsureCanProceed();
    }

    [Fact]
    public async Task TryAcquireProbeSlot_WhenAuthHealthy_AlwaysAllows()
    {
        var gate = NewGate(out _);
        await gate.Handler.HandleSuccessAsync();

        Assert.True(gate.TryAcquireProbeSlot());
        Assert.True(gate.TryAcquireProbeSlot()); // unlimited when healthy
    }

    [Fact]
    public async Task TryAcquireProbeSlot_WhenAuthBad_AllowsExactlyOnePerInterval()
    {
        var gate = NewGate(out var clock, probeInterval: TimeSpan.FromSeconds(60));
        await gate.Handler.HandleFailureAsync(new AuthFailure { Message = "fail" });

        Assert.True(gate.TryAcquireProbeSlot(), "first probe should be allowed so we can detect re-auth");
        Assert.False(gate.TryAcquireProbeSlot(), "second probe within interval must be blocked");

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(gate.TryAcquireProbeSlot(), "still inside the interval");

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(gate.TryAcquireProbeSlot(), "after the interval elapses, one more probe is allowed");
        Assert.False(gate.TryAcquireProbeSlot(), "and we're rate-limited again");
    }

    [Fact]
    public async Task TryAcquireProbeSlot_AfterRecoverySuccess_ResetsImmediately()
    {
        var gate = NewGate(out _);
        await gate.Handler.HandleFailureAsync(new AuthFailure { Message = "fail" });
        Assert.True(gate.TryAcquireProbeSlot());
        Assert.False(gate.TryAcquireProbeSlot());

        await gate.Handler.HandleSuccessAsync();

        Assert.True(gate.TryAcquireProbeSlot(), "after success the gate is fully open again");
    }

    [Fact]
    public async Task AuthGatedException_CarriesRetryAfter()
    {
        var gate = NewGate(out _, probeInterval: TimeSpan.FromSeconds(30));
        await gate.Handler.HandleFailureAsync(new AuthFailure { Message = "fail" });
        gate.TryAcquireProbeSlot(); // consume the slot

        var ex = Assert.Throws<AuthGatedException>(() => gate.EnsureCanProceed());

        Assert.NotNull(ex.RetryAfter);
        Assert.InRange(ex.RetryAfter!.Value, TimeSpan.FromSeconds(28), TimeSpan.FromSeconds(31));
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
