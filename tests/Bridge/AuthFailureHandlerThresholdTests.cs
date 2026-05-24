using System;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// R2-10: DefaultAuthFailureHandler currently latches the gate on the FIRST
/// 401. Cloudflare/edge flakes and load-shed bursts produce occasional 401s
/// that aren't real auth failures, so a single-event latch creates 60s
/// outages from transient noise. The fix exposes a configurable
/// FailureThreshold: latching only fires after K failures within a window
/// without an intervening success.
/// </summary>
public sealed class AuthFailureHandlerThresholdTests
{
    [Fact]
    public async Task DefaultThreshold_LatchesOnFirstFailure_BackCompat()
    {
        var handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance);
        await handler.HandleFailureAsync(new AuthFailure { Message = "bad" });
        Assert.Equal(AuthStatus.Failed, handler.Status);
    }

    [Fact]
    public async Task Threshold3_RequiresThreeFailuresBeforeLatching()
    {
        var handler = new DefaultAuthFailureHandler(
            NullLogger<DefaultAuthFailureHandler>.Instance,
            failureThreshold: 3);

        await handler.HandleFailureAsync(new AuthFailure { Message = "1st flake" });
        Assert.NotEqual(AuthStatus.Failed, handler.Status); // not yet

        await handler.HandleFailureAsync(new AuthFailure { Message = "2nd flake" });
        Assert.NotEqual(AuthStatus.Failed, handler.Status); // not yet

        await handler.HandleFailureAsync(new AuthFailure { Message = "3rd — latches" });
        Assert.Equal(AuthStatus.Failed, handler.Status);
    }

    [Fact]
    public async Task Threshold3_SuccessIntervened_ResetsFailureCount()
    {
        // Real-world: 2 transient 401s, then a 200 (auth was fine all along),
        // then another flake — must NOT latch (the streak was broken).
        var handler = new DefaultAuthFailureHandler(
            NullLogger<DefaultAuthFailureHandler>.Instance,
            failureThreshold: 3);

        await handler.HandleFailureAsync(new AuthFailure { Message = "1" });
        await handler.HandleFailureAsync(new AuthFailure { Message = "2" });
        await handler.HandleSuccessAsync(); // streak broken
        await handler.HandleFailureAsync(new AuthFailure { Message = "1 of new streak" });

        Assert.NotEqual(AuthStatus.Failed, handler.Status);
    }

    [Fact]
    public async Task Threshold3_LastFailureCapturedEvenWhenSubthreshold()
    {
        // LastFailure should always reflect the most recent failure, even
        // when the status is still Unknown/Authenticated (sub-threshold).
        // Otherwise observability is silent on the leading flakes.
        var handler = new DefaultAuthFailureHandler(
            NullLogger<DefaultAuthFailureHandler>.Instance,
            failureThreshold: 3);

        await handler.HandleFailureAsync(new AuthFailure { ErrorCode = "401", Message = "flake-A" });
        Assert.Equal("flake-A", handler.LastFailure?.Message);

        await handler.HandleFailureAsync(new AuthFailure { ErrorCode = "401", Message = "flake-B" });
        Assert.Equal("flake-B", handler.LastFailure?.Message);
    }

    [Fact]
    public void Threshold_LessThanOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DefaultAuthFailureHandler(
                NullLogger<DefaultAuthFailureHandler>.Instance,
                failureThreshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DefaultAuthFailureHandler(
                NullLogger<DefaultAuthFailureHandler>.Instance,
                failureThreshold: -1));
    }

    // ─── Round 3 regressions ─────────────────────────────────────────────

    [Fact]
    public async Task Threshold3_SubThresholdFailures_StillTrigger_GateRateLimit()
    {
        // R3-1: with threshold=3 and the gate above the handler, failures 1
        // and 2 don't change status to Failed → gate.IsHealthy returns true →
        // every subsequent call bypasses EnsureCanProceed and pounds the
        // upstream. K-of-N must NOT re-enable the IP-ban scenario in the
        // sub-threshold window.
        //
        // Contract: ConsecutiveFailureCount must be exposed so the gate can
        // apply probe-interval rate-limiting from the FIRST observed failure,
        // not from the K-th.
        var handler = new DefaultAuthFailureHandler(
            NullLogger<DefaultAuthFailureHandler>.Instance,
            failureThreshold: 3);

        await handler.HandleFailureAsync(new AuthFailure { Message = "1" });
        Assert.Equal(1, handler.ConsecutiveFailureCount);

        await handler.HandleFailureAsync(new AuthFailure { Message = "2" });
        Assert.Equal(2, handler.ConsecutiveFailureCount);

        await handler.HandleSuccessAsync();
        Assert.Equal(0, handler.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task Threshold3_HandleFailure_AfterExpired_DoesNotDowngradeStatus()
    {
        // R3-12: HandleFailureAsync was silently downgrading Expired → Failed
        // when count hit threshold. Expired carries the operator-actionable
        // "user must re-auth" signal; Failed is a generic transient. The
        // downgrade lost the signal. Fix: failure must NOT downgrade Expired.
        var handler = new DefaultAuthFailureHandler(
            NullLogger<DefaultAuthFailureHandler>.Instance,
            failureThreshold: 3);

        await handler.RequestReauthenticationAsync("user revoked token");
        Assert.Equal(AuthStatus.Expired, handler.Status);

        for (var i = 0; i < 5; i++)
        {
            await handler.HandleFailureAsync(new AuthFailure { Message = $"flake {i}" });
        }

        // Status must STILL be Expired — the operator-signal is preserved.
        Assert.Equal(AuthStatus.Expired, handler.Status);
    }
}
