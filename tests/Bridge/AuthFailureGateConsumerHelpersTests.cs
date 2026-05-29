using System;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Tests for the consumer-side convenience helpers <see cref="AuthFailureGate.ShouldShortCircuit"/>
/// and <see cref="AuthFailureGate.RecordExceptionOutcome"/> — lifted from the byte-identical
/// IsAuthShortCircuited / RecordAuthOutcomeFromException helpers that applemusicarr, tidalarr
/// and qobuzarr each duplicated at their bridge entry points.
/// </summary>
public sealed class AuthFailureGateConsumerHelpersTests
{
    private static AuthFailureGate NewGate(out FakeClock clock, TimeSpan? probeInterval = null)
    {
        clock = new FakeClock(new DateTimeOffset(2026, 5, 28, 12, 0, 0, TimeSpan.Zero));
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);
        return new AuthFailureGate(handler, clock, probeInterval ?? TimeSpan.FromSeconds(60),
            NullLogger<AuthFailureGate>.Instance);
    }

    [Fact]
    public void ShouldShortCircuit_WhenHealthy_ReturnsFalse()
    {
        var gate = NewGate(out _);
        Assert.False(gate.ShouldShortCircuit()); // Unknown/healthy never short-circuits
    }

    [Fact]
    public async Task ShouldShortCircuit_WhenLatched_AllowsOneProbeThenShortCircuits()
    {
        var gate = NewGate(out _, probeInterval: TimeSpan.FromSeconds(60));
        await gate.Handler.HandleFailureAsync(new AuthFailure { ErrorCode = "401", Message = "bad token" });

        // First call after latch consumes the single per-interval probe slot → allow it through.
        Assert.False(gate.ShouldShortCircuit());
        // No slot left within the interval → short-circuit subsequent calls.
        Assert.True(gate.ShouldShortCircuit());
        Assert.True(gate.ShouldShortCircuit());
    }

    [Fact]
    public async Task ShouldShortCircuit_WhenLatched_RefreshesProbeAfterInterval()
    {
        var gate = NewGate(out var clock, probeInterval: TimeSpan.FromSeconds(60));
        await gate.Handler.HandleFailureAsync(new AuthFailure { Message = "fail" });

        Assert.False(gate.ShouldShortCircuit()); // probe 1
        Assert.True(gate.ShouldShortCircuit());  // rate-limited
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(gate.ShouldShortCircuit()); // probe 2 after interval elapses
    }

    [Fact]
    public void RecordExceptionOutcome_WhenClassifyReturnsNull_DoesNotLatch()
    {
        var gate = NewGate(out _);
        gate.RecordExceptionOutcome(new InvalidOperationException("not an auth problem"), _ => null);

        Assert.True(gate.IsHealthy);
        gate.EnsureCanProceed(); // must not throw
    }

    [Fact]
    public void RecordExceptionOutcome_WhenClassifyReturnsFailure_Latches()
    {
        var gate = NewGate(out _);
        var ex = new System.Net.Http.HttpRequestException("unauthorized", null, System.Net.HttpStatusCode.Unauthorized);

        gate.RecordExceptionOutcome(ex, e => new AuthFailure
        {
            ErrorCode = (e as System.Net.Http.HttpRequestException)?.StatusCode?.ToString(),
            Message = e.Message,
        });

        Assert.False(gate.IsHealthy);
        var thrown = Assert.Throws<AuthGatedException>(() => gate.EnsureCanProceed());
        Assert.Equal("Unauthorized", thrown.ErrorCode);
    }

    [Fact]
    public void RecordExceptionOutcome_NullException_Throws()
    {
        var gate = NewGate(out _);
        Assert.Throws<ArgumentNullException>(() => gate.RecordExceptionOutcome(null!, _ => null));
    }

    [Fact]
    public void RecordExceptionOutcome_NullClassify_Throws()
    {
        var gate = NewGate(out _);
        Assert.Throws<ArgumentNullException>(() => gate.RecordExceptionOutcome(new Exception(), null!));
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
