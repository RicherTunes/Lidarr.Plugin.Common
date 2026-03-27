using System;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Bridge;
using Lidarr.Plugin.Common.TestKit.Fixtures;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Tests for <see cref="DefaultRateLimitReporter"/> — edge cases and concrete implementation
/// details. Interface-level lifecycle tests live in CoreCapabilityComplianceTests and
/// BridgeComplianceTests.
/// </summary>
public class DefaultRateLimitReporterTests : IDisposable
{
    private readonly BridgeComplianceFixture _fixture = new();

    [Fact]
    public async Task ZeroRetryAfter_SetsResetAtNearNow()
    {
        var before = DateTimeOffset.UtcNow;

        await _fixture.RateLimitReporter.ReportRateLimitAsync(TimeSpan.Zero);

        Assert.True(_fixture.RateLimitReporter.Status.IsRateLimited);
        var resetAt = _fixture.RateLimitReporter.Status.ResetAt;
        Assert.NotNull(resetAt);

        // ResetAt should be approximately "now" — within a small tolerance
        var drift = resetAt!.Value - before;
        Assert.True(drift.TotalSeconds >= 0 && drift.TotalSeconds < 5,
            $"Expected ResetAt near now, but drift was {drift.TotalSeconds:F3}s");
    }

    [Fact]
    public async Task NegativeRetryAfter_Accepted_SetsResetAtInPast()
    {
        // The default implementation does not validate negative TimeSpan values.
        // This is intentional: the bridge contract is a reporting surface, not a
        // policy enforcer. A negative retryAfter simply computes a ResetAt in the
        // past, which callers can interpret as "already expired."
        var before = DateTimeOffset.UtcNow;

        await _fixture.RateLimitReporter.ReportRateLimitAsync(TimeSpan.FromSeconds(-10));

        Assert.True(_fixture.RateLimitReporter.Status.IsRateLimited);
        var resetAt = _fixture.RateLimitReporter.Status.ResetAt;
        Assert.NotNull(resetAt);

        // ResetAt should be in the past relative to when we called
        Assert.True(resetAt!.Value < before,
            $"Expected ResetAt in the past, but got {resetAt.Value} (before={before})");
    }

    public void Dispose() => _fixture.Dispose();
}
