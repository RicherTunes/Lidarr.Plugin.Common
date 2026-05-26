using System;
using Lidarr.Plugin.Common.Services.Performance;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// UniversalAdaptiveRateLimiter previously had a single parameterless constructor
/// that hardcoded per-service defaults (Tidal=300, Qobuz=500, AppleMusic=200, etc.).
/// Plugins exposing a "Requests per second" setting (applemusicarr) had no way
/// to feed the user value through to the limiter — the setting was disclosed
/// in the UI as "informational" because the wiring did not exist.
///
/// These tests pin the new <see cref="UniversalAdaptiveRateLimiterOptions"/>
/// shape: an options object that lets callers override per-service caps. The
/// limiter ctor accepts the options and resolves per-service config from them
/// first, falling back to the built-in defaults.
/// </summary>
public sealed class UniversalAdaptiveRateLimiterOptionsTests
{
    [Fact]
    public void Ctor_NoOptions_UsesBuiltInAppleMusicDefault()
    {
        // Apple Music's built-in default is 200 req/min; that's what consumers got
        // pre-options. This test pins the backward-compatible default so a future
        // refactor doesn't silently change behaviour for plugins that don't yet
        // pass options.
        using var limiter = new UniversalAdaptiveRateLimiter();
        Assert.Equal(200, limiter.GetCurrentLimit("AppleMusic", "/v1/catalog"));
    }

    [Fact]
    public void Ctor_WithOptions_AppliesPerServiceRequestsPerMinute()
    {
        var options = new UniversalAdaptiveRateLimiterOptions()
            .WithServiceLimit("AppleMusic", requestsPerMinute: 60);

        using var limiter = new UniversalAdaptiveRateLimiter(options);

        Assert.Equal(60, limiter.GetCurrentLimit("AppleMusic", "/v1/catalog"));
    }

    [Fact]
    public void Ctor_WithOptions_OnlyOverridesNamedServices()
    {
        // Overriding AppleMusic must NOT change Tidal/Qobuz defaults — different
        // plugins share one limiter implementation but each owns its own service
        // name and shouldn't accidentally throttle each other.
        var options = new UniversalAdaptiveRateLimiterOptions()
            .WithServiceLimit("AppleMusic", requestsPerMinute: 60);

        using var limiter = new UniversalAdaptiveRateLimiter(options);

        Assert.Equal(60, limiter.GetCurrentLimit("AppleMusic", "/v1/catalog"));
        Assert.Equal(300, limiter.GetCurrentLimit("Tidal", "/v1/search"));
        Assert.Equal(500, limiter.GetCurrentLimit("Qobuz", "/api.json/0.2/album"));
    }

    [Fact]
    public void WithServiceLimit_NullOrWhitespaceService_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new UniversalAdaptiveRateLimiterOptions().WithServiceLimit("", 100));
        Assert.Throws<ArgumentException>(() =>
            new UniversalAdaptiveRateLimiterOptions().WithServiceLimit("   ", 100));
        Assert.Throws<ArgumentException>(() =>
            new UniversalAdaptiveRateLimiterOptions().WithServiceLimit(null!, 100));
    }

    [Fact]
    public void WithServiceLimit_NonPositiveRpm_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UniversalAdaptiveRateLimiterOptions().WithServiceLimit("AppleMusic", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UniversalAdaptiveRateLimiterOptions().WithServiceLimit("AppleMusic", -1));
    }

    [Fact]
    public void WithServiceLimit_ReturnsSameInstance_FluentChaining()
    {
        var options = new UniversalAdaptiveRateLimiterOptions();
        var chained = options
            .WithServiceLimit("AppleMusic", 60)
            .WithServiceLimit("Qobuz", 300);

        Assert.Same(options, chained);
    }

    [Fact]
    public void WithServiceLimit_OverridesAreCaseInsensitive()
    {
        // Service names get matched case-insensitively against the per-service
        // limiter dictionary (see UniversalAdaptiveRateLimiter._serviceLimiters
        // using StringComparer.OrdinalIgnoreCase). Options must match.
        var options = new UniversalAdaptiveRateLimiterOptions()
            .WithServiceLimit("APPLEMUSIC", requestsPerMinute: 60);

        using var limiter = new UniversalAdaptiveRateLimiter(options);

        Assert.Equal(60, limiter.GetCurrentLimit("AppleMusic", "/v1/catalog"));
        Assert.Equal(60, limiter.GetCurrentLimit("applemusic", "/v1/catalog"));
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UniversalAdaptiveRateLimiter(null!));
    }

    [Fact]
    public void WithServiceLimit_RpmConvertsToReasonableAdaptiveBounds()
    {
        // A user-specified rate-per-minute becomes the limiter's starting/default
        // rate. Adaptive bounds are derived around it so backoff has room to move:
        // - min: 30% of configured rpm (or 1, whichever is higher)
        // - max: 130% of configured rpm
        //
        // GetCurrentLimit() returns the *current* rate which starts at the default.
        var options = new UniversalAdaptiveRateLimiterOptions()
            .WithServiceLimit("AppleMusic", requestsPerMinute: 100);

        using var limiter = new UniversalAdaptiveRateLimiter(options);

        // Initial limit is the configured value (no adaptive movement yet).
        Assert.Equal(100, limiter.GetCurrentLimit("AppleMusic", "/v1/catalog"));
    }

    [Fact]
    public void WithServiceLimit_VeryLowRpm_StillProducesValidConfig()
    {
        // User sets 1 req/min — common case for a paranoid first-run.
        // The 30% min rule would round to 0; clamp to 1 so the limiter never
        // produces a zero-floor config that would block all traffic.
        var options = new UniversalAdaptiveRateLimiterOptions()
            .WithServiceLimit("AppleMusic", requestsPerMinute: 1);

        using var limiter = new UniversalAdaptiveRateLimiter(options);

        Assert.Equal(1, limiter.GetCurrentLimit("AppleMusic", "/v1/catalog"));
    }
}
