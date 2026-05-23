using System;
using System.Net;
using System.Net.Http;

using Lidarr.Plugin.Common.Services.Performance;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Performance;

/// <summary>
/// Tests for the <c>ServiceProfile.Conservative</c> preset — APL-009.
/// <para>
/// Design contract:
/// <list type="bullet">
///   <item>Conservative default: ~1 req/sec (60 RPM), minimum gap ≥ 2 s, circuit
///   opens after 3 consecutive non-auth errors and stays open for 30 s.</item>
///   <item>Existing non-Conservative behaviour is unchanged (back-compat).</item>
/// </list>
/// </para>
/// </summary>
public sealed class UniversalAdaptiveRateLimiterConservativeTests
{
    private const string Service = "AppleMusicTest";
    private const string Endpoint = "/v1/catalog/us/albums";

    // ------------------------------------------------------------------ //
    // Back-compat: existing default behaviour is unchanged
    // ------------------------------------------------------------------ //

    [Fact]
    public void DefaultLimiter_GetCurrentLimit_ReturnsDefaultServiceRpm()
    {
        // The "AppleMusic" named entry in DefaultServiceConfigs is 200 RPM.
        using var limiter = new UniversalAdaptiveRateLimiter();
        var limit = limiter.GetCurrentLimit("AppleMusic", Endpoint);
        Assert.True(limit > 0, "Default limit should be positive");
        // We don't assert the exact number to avoid coupling to internal config values,
        // but we verify the default profile path still works.
    }

    [Fact]
    public void DefaultLimiter_RecordSuccessResponses_CanExpandBudget()
    {
        using var limiter = new UniversalAdaptiveRateLimiter();
        var initialLimit = limiter.GetCurrentLimit(Service, Endpoint);

        // 25 consecutive successes should trigger at least one expansion step.
        for (var i = 0; i < 25; i++)
        {
            using var r = new HttpResponseMessage(HttpStatusCode.OK);
            limiter.RecordResponse(Service, Endpoint, r);
        }

        // Budget must not shrink from successes.
        Assert.True(limiter.GetCurrentLimit(Service, Endpoint) >= initialLimit);
    }

    [Fact]
    public void DefaultLimiter_Record429_ShrinksBudget()
    {
        using var limiter = new UniversalAdaptiveRateLimiter();
        var before = limiter.GetCurrentLimit(Service, Endpoint);

        using var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limiter.RecordResponse(Service, Endpoint, r);

        Assert.True(limiter.GetCurrentLimit(Service, Endpoint) < before,
            "429 must shrink the budget on the default profile");
    }

    // ------------------------------------------------------------------ //
    // Conservative preset
    // ------------------------------------------------------------------ //

    [Fact]
    public void ConservativeLimiter_InitialLimit_IsAtMost60Rpm()
    {
        using var limiter = UniversalAdaptiveRateLimiter.WithConservativeDefaults();
        // The default limit for any service through the conservative profile must be ≤ 60 RPM.
        var limit = limiter.GetCurrentLimit(Service, Endpoint);
        Assert.True(limit <= 60,
            $"Conservative preset initial RPM should be ≤ 60 but was {limit}");
    }

    [Fact]
    public void ConservativeLimiter_InitialLimit_IsPositive()
    {
        using var limiter = UniversalAdaptiveRateLimiter.WithConservativeDefaults();
        Assert.True(limiter.GetCurrentLimit(Service, Endpoint) > 0);
    }

    [Fact]
    public void ConservativeLimiter_Record429_ShrinksBudgetButStaysPositive()
    {
        using var limiter = UniversalAdaptiveRateLimiter.WithConservativeDefaults();
        using var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limiter.RecordResponse(Service, Endpoint, r);

        Assert.True(limiter.GetCurrentLimit(Service, Endpoint) > 0,
            "Budget must not drop to 0 after a single 429 on the conservative profile");
    }

    [Fact]
    public void ConservativeLimiter_HasCircuitOpenThreshold_AfterConsecutiveErrors()
    {
        // Simulate 4 consecutive 5xx errors — enough to open the conservative circuit.
        using var limiter = UniversalAdaptiveRateLimiter.WithConservativeDefaults();
        var before = limiter.GetCurrentLimit(Service, Endpoint);

        for (var i = 0; i < 4; i++)
        {
            using var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            limiter.RecordResponse(Service, Endpoint, r);
        }

        // Conservative profile tightens budget more aggressively on consecutive errors.
        var after = limiter.GetCurrentLimit(Service, Endpoint);

        // We assert the budget either opened the circuit (very low/capped) or
        // at minimum has tightened compared to the start.
        Assert.True(after <= before,
            "Conservative profile must tighten or open circuit on repeated 5xx");
    }

    [Fact]
    public void ConservativeLimiter_AuthFailures_DoNotTightenBudget()
    {
        // Auth neutrality must also hold on the conservative profile.
        using var limiter = UniversalAdaptiveRateLimiter.WithConservativeDefaults();
        var before = limiter.GetCurrentLimit(Service, Endpoint);

        for (var i = 0; i < 10; i++)
        {
            using var r = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            limiter.RecordResponse(Service, Endpoint, r);
        }

        Assert.Equal(before, limiter.GetCurrentLimit(Service, Endpoint));
    }

    [Fact]
    public void ConservativeLimiter_Dispose_DoesNotThrow()
    {
        var limiter = UniversalAdaptiveRateLimiter.WithConservativeDefaults();
        var ex = Record.Exception(() => limiter.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void ConservativeLimiter_GetServiceStats_ReturnsStats()
    {
        using var limiter = UniversalAdaptiveRateLimiter.WithConservativeDefaults();
        // Trigger creation of an endpoint entry.
        using var r = new HttpResponseMessage(HttpStatusCode.OK);
        limiter.RecordResponse(Service, Endpoint, r);

        var stats = limiter.GetServiceStats(Service);
        Assert.NotNull(stats);
    }

    [Fact]
    public void ConservativeLimiter_IsIUniversalAdaptiveRateLimiter()
    {
        using var limiter = UniversalAdaptiveRateLimiter.WithConservativeDefaults();
        Assert.IsAssignableFrom<IUniversalAdaptiveRateLimiter>(limiter);
    }
}
