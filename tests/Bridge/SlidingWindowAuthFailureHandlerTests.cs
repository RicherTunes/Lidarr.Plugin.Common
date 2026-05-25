using System;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Tests for <see cref="SlidingWindowAuthFailureHandler"/>: K-of-N-in-W sliding-window
/// circuit-breaker semantics that brainarr's <c>LlmAuthCircuit</c> needs (3 failures
/// in 5 min → latch). Pairs with <see cref="AuthFailureGate"/>'s probeInterval to give
/// the brainarr-style "open for D, probe, close-or-reopen" behaviour using only the
/// shared Common API surface.
/// </summary>
public sealed class SlidingWindowAuthFailureHandlerTests
{
    private static SlidingWindowAuthFailureHandler NewHandler(
        out FakeClock clock,
        int failureThreshold = 3,
        TimeSpan? failureWindow = null)
    {
        clock = new FakeClock(new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero));
        return new SlidingWindowAuthFailureHandler(
            NullLogger<SlidingWindowAuthFailureHandler>.Instance,
            failureThreshold,
            failureWindow ?? TimeSpan.FromMinutes(5),
            clock);
    }

    private static AuthFailure Failure(string code = "401", string message = "Unauthorized") =>
        new() { ErrorCode = code, Message = message };

    // ─── Constructor validation ───────────────────────────────────────────

    [Fact]
    public void Constructor_RejectsNullLogger()
    {
        Assert.Throws<ArgumentNullException>(() => new SlidingWindowAuthFailureHandler(null!));
    }

    [Fact]
    public void Constructor_RejectsZeroOrNegativeFailureThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SlidingWindowAuthFailureHandler(NullLogger<SlidingWindowAuthFailureHandler>.Instance, failureThreshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SlidingWindowAuthFailureHandler(NullLogger<SlidingWindowAuthFailureHandler>.Instance, failureThreshold: -1));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveFailureWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowAuthFailureHandler(
            NullLogger<SlidingWindowAuthFailureHandler>.Instance,
            failureThreshold: 3,
            failureWindow: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowAuthFailureHandler(
            NullLogger<SlidingWindowAuthFailureHandler>.Instance,
            failureThreshold: 3,
            failureWindow: TimeSpan.FromSeconds(-1)));
    }

    // ─── Initial state ────────────────────────────────────────────────────

    [Fact]
    public void InitialStatus_IsUnknown()
    {
        var h = NewHandler(out _);
        Assert.Equal(AuthStatus.Unknown, h.Status);
        Assert.Null(h.LastFailure);
        Assert.Equal(0, h.ConsecutiveFailureCount);
        Assert.Null(h.FirstFailureInWindow);
    }

    // ─── Threshold latch ──────────────────────────────────────────────────

    [Fact]
    public async Task SubThreshold_DoesNotLatch()
    {
        var h = NewHandler(out _, failureThreshold: 3);
        await h.HandleFailureAsync(Failure());
        await h.HandleFailureAsync(Failure());

        Assert.Equal(AuthStatus.Unknown, h.Status);
        Assert.Equal(2, h.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task AtThreshold_LatchesToFailed()
    {
        var h = NewHandler(out _, failureThreshold: 3);
        await h.HandleFailureAsync(Failure());
        await h.HandleFailureAsync(Failure());
        await h.HandleFailureAsync(Failure());

        Assert.Equal(AuthStatus.Failed, h.Status);
        Assert.Equal(3, h.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task BeyondThreshold_StaysFailed()
    {
        var h = NewHandler(out _, failureThreshold: 3);
        for (int i = 0; i < 5; i++)
        {
            await h.HandleFailureAsync(Failure());
        }
        Assert.Equal(AuthStatus.Failed, h.Status);
        Assert.Equal(5, h.ConsecutiveFailureCount);
    }

    // ─── Sliding window ───────────────────────────────────────────────────

    [Fact]
    public async Task FailuresOutsideWindow_DoNotCountTowardThreshold()
    {
        // 2 failures, then >5 min later 1 failure should NOT trigger the threshold
        // (the first 2 are outside the sliding window).
        var h = NewHandler(out var clock, failureThreshold: 3, failureWindow: TimeSpan.FromMinutes(5));

        await h.HandleFailureAsync(Failure());
        await h.HandleFailureAsync(Failure());
        Assert.Equal(2, h.ConsecutiveFailureCount);

        clock.Advance(TimeSpan.FromMinutes(6));

        await h.HandleFailureAsync(Failure());
        Assert.Equal(AuthStatus.Unknown, h.Status); // not latched
        Assert.Equal(1, h.ConsecutiveFailureCount); // window reset
    }

    [Fact]
    public async Task FailuresWithinWindow_AccumulateNormally()
    {
        var h = NewHandler(out var clock, failureThreshold: 3, failureWindow: TimeSpan.FromMinutes(5));

        await h.HandleFailureAsync(Failure());
        clock.Advance(TimeSpan.FromMinutes(2));
        await h.HandleFailureAsync(Failure());
        clock.Advance(TimeSpan.FromMinutes(2));
        await h.HandleFailureAsync(Failure());

        // All 3 within the 5-min window (last one at +4 min from first)
        Assert.Equal(AuthStatus.Failed, h.Status);
        Assert.Equal(3, h.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task WindowSlides_OnlyFromFirstFailure()
    {
        // The window is anchored to the FIRST failure in the current run, not a
        // rolling per-failure window. After 5min from the first, a new failure
        // starts a fresh run.
        var h = NewHandler(out var clock, failureThreshold: 3, failureWindow: TimeSpan.FromMinutes(5));

        await h.HandleFailureAsync(Failure()); // t=0
        clock.Advance(TimeSpan.FromMinutes(3));
        await h.HandleFailureAsync(Failure()); // t=3min (still within window)
        clock.Advance(TimeSpan.FromMinutes(3));
        // t=6min — outside the original 5-min window from the first failure at t=0.
        // The counter resets, this is a new run of 1.
        await h.HandleFailureAsync(Failure());

        Assert.Equal(AuthStatus.Unknown, h.Status);
        Assert.Equal(1, h.ConsecutiveFailureCount);
    }

    // ─── Success recovery ─────────────────────────────────────────────────

    [Fact]
    public async Task Success_FromUnknown_TransitionsToAuthenticated()
    {
        var h = NewHandler(out _);
        await h.HandleSuccessAsync();
        Assert.Equal(AuthStatus.Authenticated, h.Status);
    }

    [Fact]
    public async Task Success_FromFailed_TransitionsToAuthenticated_ResetsCounter()
    {
        var h = NewHandler(out _, failureThreshold: 3);
        await h.HandleFailureAsync(Failure());
        await h.HandleFailureAsync(Failure());
        await h.HandleFailureAsync(Failure());
        Assert.Equal(AuthStatus.Failed, h.Status);

        await h.HandleSuccessAsync();

        Assert.Equal(AuthStatus.Authenticated, h.Status);
        Assert.Equal(0, h.ConsecutiveFailureCount);
        Assert.Null(h.FirstFailureInWindow);
        Assert.Null(h.LastFailure);
    }

    [Fact]
    public async Task Success_IsIdempotent()
    {
        var h = NewHandler(out _);
        await h.HandleSuccessAsync();
        await h.HandleSuccessAsync();
        await h.HandleSuccessAsync();
        Assert.Equal(AuthStatus.Authenticated, h.Status);
    }

    // ─── Expired status preservation ──────────────────────────────────────

    [Fact]
    public async Task Failure_AtThreshold_DoesNotDowngradeExpiredToFailed()
    {
        var h = NewHandler(out _, failureThreshold: 1);
        // RequestReauthentication marks Expired (the stronger, more actionable signal).
        await h.RequestReauthenticationAsync("user revoked token");
        Assert.Equal(AuthStatus.Expired, h.Status);

        // A subsequent failure must not downgrade to Failed (Failed is generic transient;
        // Expired carries the operator-actionable "user must re-auth" signal).
        await h.HandleFailureAsync(Failure());
        Assert.Equal(AuthStatus.Expired, h.Status);
    }

    // ─── ResetToUnknown ───────────────────────────────────────────────────

    [Fact]
    public async Task ResetToUnknown_ClearsLatchAndCounter()
    {
        var h = NewHandler(out _, failureThreshold: 3);
        await h.HandleFailureAsync(Failure());
        await h.HandleFailureAsync(Failure());
        await h.HandleFailureAsync(Failure());
        Assert.Equal(AuthStatus.Failed, h.Status);

        h.ResetToUnknown();

        Assert.Equal(AuthStatus.Unknown, h.Status);
        Assert.Equal(0, h.ConsecutiveFailureCount);
        Assert.Null(h.FirstFailureInWindow);
        Assert.Null(h.LastFailure);
    }

    // ─── Integration with AuthFailureGate ─────────────────────────────────

    [Fact]
    public async Task IntegratesWithAuthFailureGate_ThresholdLatchTriggersGate()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero));
        var handler = new SlidingWindowAuthFailureHandler(
            NullLogger<SlidingWindowAuthFailureHandler>.Instance,
            failureThreshold: 3,
            failureWindow: TimeSpan.FromMinutes(5),
            clock);
        var gate = new AuthFailureGate(
            handler,
            clock,
            // openDuration = probeInterval — gate grants one probe slot per 30 min while latched.
            probeInterval: TimeSpan.FromMinutes(30),
            NullLogger<AuthFailureGate>.Instance);

        // Sub-threshold failures don't latch the gate.
        await handler.HandleFailureAsync(Failure());
        await handler.HandleFailureAsync(Failure());
        Assert.False(handler.ConsecutiveFailureCount == 0);
        // Gate's IsHealthy is false during sub-threshold (Common's "K-of-N pre-latch rate limit"),
        // but EnsureCanProceed only throws when status is Failed/Expired. Sub-threshold status is
        // still Unknown — caller can proceed.
        gate.EnsureCanProceed();

        // Third failure crosses the threshold.
        await handler.HandleFailureAsync(Failure());
        Assert.Equal(AuthStatus.Failed, handler.Status);

        // Gate now short-circuits via AuthGatedException.
        Assert.Throws<AuthGatedException>(() => gate.EnsureCanProceed());

        // First call after latch gets the probe slot.
        Assert.True(gate.TryAcquireProbeSlot());
        // Subsequent calls within probeInterval are rate-limited.
        Assert.False(gate.TryAcquireProbeSlot());

        // After openDuration (probeInterval) elapses, a new probe slot is granted.
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.True(gate.TryAcquireProbeSlot());
    }

    [Fact]
    public async Task IntegratesWithAuthFailureGate_SuccessRecoversGate()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero));
        var handler = new SlidingWindowAuthFailureHandler(
            NullLogger<SlidingWindowAuthFailureHandler>.Instance,
            failureThreshold: 3,
            failureWindow: TimeSpan.FromMinutes(5),
            clock);
        var gate = new AuthFailureGate(
            handler,
            clock,
            probeInterval: TimeSpan.FromMinutes(30),
            NullLogger<AuthFailureGate>.Instance);

        // Latch the gate.
        for (int i = 0; i < 3; i++)
        {
            await handler.HandleFailureAsync(Failure());
        }
        Assert.Throws<AuthGatedException>(() => gate.EnsureCanProceed());

        // Successful probe recovers the gate.
        await handler.HandleSuccessAsync();
        gate.EnsureCanProceed(); // does not throw
        Assert.True(gate.IsHealthy);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
