using System.Net;
using System.Net.Http;

using Lidarr.Plugin.Common.Services.Performance;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Performance;

/// <summary>
/// Tests that the rate limiter treats authentication failures (401/403)
/// distinctly from transient errors. Driver: the qobuz IP-ban audit found
/// that RecordResponse incremented the rate-limit budget on every 401,
/// causing the per-endpoint cap to tighten even though the upstream had
/// plenty of capacity — the issue was credentials, not load.
/// AuthFailureGate already handles short-circuiting; the rate limiter must
/// stop interfering with the budget on auth failures.
/// </summary>
public sealed class UniversalAdaptiveRateLimiterAuthAwarenessTests
{
    private const string Service = "TestService";
    private const string Endpoint = "/v1/test";

    private static UniversalAdaptiveRateLimiter NewLimiter()
        => new UniversalAdaptiveRateLimiter();

    private static HttpResponseMessage Resp(HttpStatusCode code) => new HttpResponseMessage(code);

    [Fact]
    public void RecordResponse_OnRepeated401_DoesNotTightenRateLimitBudget()
    {
        // Adversarial driver: with the old behavior, 5 consecutive 401s
        // (typical for an expired-token search-loop scenario) crossed the
        // ConsecutiveErrors >= 5 threshold and shrank the per-endpoint RPM.
        // After the fix, auth failures must not influence the budget.
        using var limiter = NewLimiter();
        var initialLimit = limiter.GetCurrentLimit(Service, Endpoint);

        for (var i = 0; i < 10; i++)
        {
            using var r = Resp(HttpStatusCode.Unauthorized);
            limiter.RecordResponse(Service, Endpoint, r);
        }

        Assert.Equal(initialLimit, limiter.GetCurrentLimit(Service, Endpoint));
    }

    [Fact]
    public void RecordResponse_OnRepeated403_DoesNotTightenRateLimitBudget()
    {
        using var limiter = NewLimiter();
        var initialLimit = limiter.GetCurrentLimit(Service, Endpoint);

        for (var i = 0; i < 10; i++)
        {
            using var r = Resp(HttpStatusCode.Forbidden);
            limiter.RecordResponse(Service, Endpoint, r);
        }

        Assert.Equal(initialLimit, limiter.GetCurrentLimit(Service, Endpoint));
    }

    [Fact]
    public void RecordResponse_On429_StillTightensBudget()
    {
        // 429 is the actual capacity signal — must still tighten.
        using var limiter = NewLimiter();
        var initialLimit = limiter.GetCurrentLimit(Service, Endpoint);

        using var r = Resp(HttpStatusCode.TooManyRequests);
        limiter.RecordResponse(Service, Endpoint, r);

        Assert.True(limiter.GetCurrentLimit(Service, Endpoint) < initialLimit,
            "429 must shrink the per-endpoint RPM");
    }

    [Fact]
    public void RecordResponse_On5xxBurst_StillTightensBudget_After5Consecutive()
    {
        // 5xx are transient errors — the existing 5-consecutive threshold
        // for budget tightening still applies for these.
        using var limiter = NewLimiter();
        var initialLimit = limiter.GetCurrentLimit(Service, Endpoint);

        for (var i = 0; i < 6; i++)
        {
            using var r = Resp(HttpStatusCode.InternalServerError);
            limiter.RecordResponse(Service, Endpoint, r);
        }

        Assert.True(limiter.GetCurrentLimit(Service, Endpoint) < initialLimit,
            "Sustained 5xx must shrink the per-endpoint RPM");
    }

    [Fact]
    public void RecordResponse_401MixedWith200s_DoesNotResetSuccessStreak()
    {
        // The old behavior: a 401 inside a success streak reset
        // ConsecutiveSuccesses, delaying budget expansion. After the fix,
        // 401s are neutral — they neither tighten nor reset success tracking.
        using var limiter = NewLimiter();

        // Run successes interspersed with auth failures.
        for (var i = 0; i < 25; i++)
        {
            using var ok = Resp(HttpStatusCode.OK);
            limiter.RecordResponse(Service, Endpoint, ok);
            if (i % 5 == 0)
            {
                using var bad = Resp(HttpStatusCode.Unauthorized);
                limiter.RecordResponse(Service, Endpoint, bad);
            }
        }

        // With 25 successes the budget would have expanded (≥20 streak → ×1.2).
        // The 5 interspersed 401s would have reset the streak under old code
        // and prevented expansion; under the fix, expansion should happen.
        var initialDefault = new UniversalAdaptiveRateLimiter().GetCurrentLimit(Service, Endpoint);
        Assert.True(limiter.GetCurrentLimit(Service, Endpoint) > initialDefault,
            "auth failures must not reset the success streak that drives budget expansion");
    }

    [Fact]
    public void RecordAuthFailure_NewExplicitMethod_DoesNotChangeBudget()
    {
        // Provide an explicit method so callers that detect auth failure outside
        // the HTTP layer (e.g. via exception types) can signal without minting
        // a synthetic HttpResponseMessage.
        using var limiter = NewLimiter();
        var initialLimit = limiter.GetCurrentLimit(Service, Endpoint);

        for (var i = 0; i < 10; i++)
        {
            limiter.RecordAuthFailure(Service, Endpoint);
        }

        Assert.Equal(initialLimit, limiter.GetCurrentLimit(Service, Endpoint));
    }
}
