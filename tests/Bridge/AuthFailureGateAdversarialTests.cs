using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Reproduction tests for the adversarial findings against AuthFailureGate +
/// AuthFailureDelegatingHandler. Each test corresponds to a numbered finding
/// in the review log and demonstrates the bug; the implementation must change
/// to make them green.
/// </summary>
public sealed class AuthFailureGateAdversarialTests
{
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static (AuthFailureGate Gate, DefaultAuthFailureHandler Handler, FakeClock Clock) NewGate(TimeSpan? probeInterval = null)
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero));
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);
        var gate = new AuthFailureGate(handler, clock, probeInterval ?? TimeSpan.FromSeconds(60), NullLogger<AuthFailureGate>.Instance);
        return (gate, handler, clock);
    }

    // ─── #1 Probe-slot leak when network call never executes ─────────────

    [Fact]
    public async Task ProbeSlot_NotConsumed_WhenNetworkCallIsCancelledBeforeSend()
    {
        // Adversarial #1: if the request is cancelled BEFORE base.SendAsync
        // returns a status, the probe slot was committed for nothing. We lose
        // 60s of recovery latency for a probe that never reached the upstream.
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });
        var gate = new AuthFailureGate(handler, TimeProvider.System, TimeSpan.FromMinutes(5));

        var stub = new CancellingStubHandler();
        var delegating = new AuthFailureDelegatingHandler(gate) { InnerHandler = stub };
        var client = new HttpClient(delegating) { BaseAddress = new Uri("https://test.invalid/") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/x"));

        // The slot should NOT have been consumed — the request never reached the
        // upstream, so we learned nothing about auth state. A subsequent attempt
        // must still be allowed (it gets the slot).
        Assert.True(gate.TryAcquireProbeSlot(),
            "probe slot must be refunded when no upstream response was observed");
    }

    private sealed class CancellingStubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new OperationCanceledException("simulated pre-network cancel"));
    }

    // ─── #2 Stale-CDN flap: 200 from non-auth endpoint clears latch ───────

    [Fact]
    public async Task Recovery_AfterFlap_DoesNotImmediatelyRearmProbeOnRelatch()
    {
        // Adversarial #2 (TDD-real reproduction per round-2 R2-3 finding):
        // round-1 fix passed for the wrong reason — never drove recovery through
        // the gate, so ResetIfRecovered never fired. This version exercises the
        // FULL transition path:
        //   1. Latch bad → consume probe slot
        //   2. Status flips Authenticated (200 from a public endpoint, or any
        //      external success handler call) → ResetIfRecovered fires inside
        //      TryAcquireProbeSlot and zeroes _lastProbeAt
        //   3. Re-latch bad immediately
        //   4. Probe slot grant — currently INCORRECTLY allowed because
        //      _lastProbeAt was zeroed by step 2
        var (gate, handler, clock) = NewGate(TimeSpan.FromSeconds(60));
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });

        // Probe slot consumed.
        Assert.True(gate.TryAcquireProbeSlot());
        Assert.False(gate.TryAcquireProbeSlot()); // confirm rate-limited

        // Recovery: handler flips healthy.
        await handler.HandleSuccessAsync();

        // CRITICAL: drive recovery through the gate so ResetIfRecovered fires.
        // Round-1 fix never did this — it tested handler state directly which
        // bypassed the gate's transition observer.
        Assert.True(gate.TryAcquireProbeSlot()); // healthy path returns true unconditionally

        // Flap back to bad within the original probe interval.
        await handler.HandleFailureAsync(new AuthFailure { Message = "still bad" });
        clock.Advance(TimeSpan.FromSeconds(5));

        // The desired behavior: re-latching within the probe interval must NOT
        // grant a fresh probe slot — the original probe was at t=0 and the
        // next eligible probe is at t=60s. Recovery did not advance the clock.
        Assert.False(gate.TryAcquireProbeSlot(),
            "re-latching within the probe interval must NOT grant a fresh probe slot");

        // After the full interval elapses, a probe IS allowed (slow-recurrence
        // case — not a flap).
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(gate.TryAcquireProbeSlot(),
            "after the probe interval elapses, a fresh probe is allowed even after recovery");
    }

    // ─── #4 Single-flake latching threshold ──────────────────────────────
    // Deferred — requires introducing a configurable threshold to
    // DefaultAuthFailureHandler. Covered by AuthFailureGateConfigurableThreshold
    // (next wave). For now the LATCHED-BAD behavior on the first failure is
    // documented and the contract clarification is the deferred item.

    // ─── #5 LastFailure access pattern works through interface ───────────

    [Fact]
    public async Task EnsureCanProceed_OnCustomHandler_StillCarriesFailureMessage()
    {
        // Adversarial #5: AuthFailureGate currently downcasts to
        // DefaultAuthFailureHandler to read LastFailure. A custom
        // IAuthFailureHandler should still produce an actionable exception
        // message via the gate.
        var custom = new CustomHandler();
        var gate = new AuthFailureGate(custom, TimeProvider.System, TimeSpan.FromMinutes(5));
        await custom.HandleFailureAsync(new AuthFailure { ErrorCode = "XYZ-401", Message = "custom plugin auth bad" });

        var ex = Assert.Throws<AuthGatedException>(() => gate.EnsureCanProceed());

        Assert.Contains("custom plugin auth bad", ex.Message);
        Assert.Equal("XYZ-401", ex.ErrorCode);
    }

    private sealed class CustomHandler : IAuthFailureHandler
    {
        public AuthStatus Status { get; private set; } = AuthStatus.Unknown;
        public AuthFailure? LastFailure { get; private set; }
        public ValueTask HandleFailureAsync(AuthFailure failure, CancellationToken cancellationToken = default)
        {
            LastFailure = failure;
            Status = AuthStatus.Failed;
            return ValueTask.CompletedTask;
        }
        public ValueTask HandleSuccessAsync(CancellationToken cancellationToken = default)
        {
            Status = AuthStatus.Authenticated;
            return ValueTask.CompletedTask;
        }
        public ValueTask RequestReauthenticationAsync(string reason, CancellationToken cancellationToken = default)
        {
            Status = AuthStatus.Expired;
            return ValueTask.CompletedTask;
        }
    }

    // ─── #14 ComputeRetryAfter returns interval when never probed ────────

    [Fact]
    public async Task EnsureCanProceed_WhenLatchedBadButNeverProbed_ReturnsInterval()
    {
        // Adversarial #14: when the gate is bad and never probed, callers
        // currently get RetryAfter=null. They may default to "retry now".
        // Suggest probeInterval as a safer default than null.
        var (gate, handler, _) = NewGate(TimeSpan.FromSeconds(45));
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });

        var ex = Assert.Throws<AuthGatedException>(() => gate.EnsureCanProceed());

        Assert.NotNull(ex.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(45), ex.RetryAfter!.Value);
    }

    // ─── #15 RequestReauthenticationAsync produces actionable exception ──

    [Fact]
    public async Task EnsureCanProceed_AfterRequestReauthentication_CarriesReasonAndCode()
    {
        var (gate, handler, _) = NewGate();
        await handler.RequestReauthenticationAsync("token revoked by user in dashboard");

        var ex = Assert.Throws<AuthGatedException>(() => gate.EnsureCanProceed());

        Assert.Equal(AuthStatus.Expired, ex.Status);
        Assert.Contains("token revoked", ex.Message);
        Assert.Equal("EXPIRED", ex.ErrorCode);
    }

    // ─── #16 Concurrent SendAsync against latched gate → exactly 1 net call ──

    [Fact]
    public async Task Send_50ConcurrentCallers_AgainstLatchedGate_HitNetworkExactlyOnce()
    {
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });
        var gate = new AuthFailureGate(handler, TimeProvider.System, TimeSpan.FromMinutes(5));

        var stub = new CountingStubHandler { NextStatus = HttpStatusCode.Unauthorized };
        var delegating = new AuthFailureDelegatingHandler(gate) { InnerHandler = stub };
        var client = new HttpClient(delegating) { BaseAddress = new Uri("https://test.invalid/") };

        var tasks = new Task[50];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                try { (await client.GetAsync("/x")).Dispose(); }
                catch (AuthGatedException) { /* expected for losers */ }
            });
        }
        await Task.WhenAll(tasks);

        Assert.Equal(1, stub.CallCount);
    }

    private sealed class CountingStubHandler : HttpMessageHandler
    {
        public HttpStatusCode NextStatus { get; set; } = HttpStatusCode.OK;
        private int _count;
        public int CallCount => _count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new HttpResponseMessage(NextStatus));
        }
    }

    // ─── #16 403 vs 401 should both latch (current scope; geo-block debate deferred) ──

    [Fact]
    public async Task Send_OnProbe5xx_DoesNotClaimRecovery_NorRelatchAgain()
    {
        // Adversarial: probe response is 500 — neither latches nor recovers.
        // Document by test: the gate should remain bad, and the slot should
        // be considered consumed (5xx is the upstream's failure signal —
        // we don't know if auth is still bad or not).
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });
        var gate = new AuthFailureGate(handler, TimeProvider.System, TimeSpan.FromMinutes(5));

        var stub = new CountingStubHandler { NextStatus = HttpStatusCode.InternalServerError };
        var delegating = new AuthFailureDelegatingHandler(gate) { InnerHandler = stub };
        var client = new HttpClient(delegating) { BaseAddress = new Uri("https://test.invalid/") };

        using (var r1 = await client.GetAsync("/x")) { Assert.Equal(HttpStatusCode.InternalServerError, r1.StatusCode); }
        // Status should remain Failed (5xx didn't recover).
        Assert.Equal(AuthStatus.Failed, handler.Status);

        // Second call inside probe interval still short-circuits.
        await Assert.ThrowsAsync<AuthGatedException>(() => client.GetAsync("/x"));
        Assert.Equal(1, stub.CallCount);
    }
}
