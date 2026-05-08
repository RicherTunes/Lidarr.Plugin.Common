using System;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Utilities;

/// <summary>
/// Tests for the Phase 5e <see cref="ResiliencePolicy.Passthrough"/> preset.
/// </summary>
[Trait("Category", "Unit")]
public class ResiliencePolicyPresetsTests
{
    [Fact]
    public void Passthrough_HasIdentityCharacteristics()
    {
        var p = ResiliencePolicy.Passthrough;

        Assert.Equal("passthrough", p.Name);
        // No retries beyond the initial attempt: GenericResilienceExecutor treats MaxRetries=1 as "do not retry".
        Assert.Equal(1, p.MaxRetries);
        // Effectively unbounded concurrency: the resilience layer does not gate when the transport handles it.
        Assert.Equal(int.MaxValue, p.MaxConcurrencyPerHost);
        Assert.Equal(int.MaxValue, p.MaxTotalConcurrencyPerHost);
        // No per-request timeout — defer entirely to the transport's own timeout.
        Assert.Null(p.PerRequestTimeout);
        // Backoff/jitter values are technically nonzero (constructor invariants require positive InitialBackoff)
        // but small enough to be effectively no-op since MaxRetries=1 means no backoff is ever scheduled.
        Assert.Equal(TimeSpan.FromMilliseconds(1), p.InitialBackoff);
        Assert.Equal(TimeSpan.FromMilliseconds(1), p.MaxBackoff);
        Assert.Equal(TimeSpan.Zero, p.JitterMin);
        Assert.Equal(TimeSpan.Zero, p.JitterMax);
    }

    [Fact]
    public void Passthrough_IsSingleton()
    {
        // Static property — identity should be stable across calls.
        Assert.Same(ResiliencePolicy.Passthrough, ResiliencePolicy.Passthrough);
    }

    [Fact]
    public void Passthrough_DoesNotCollideWithDefault()
    {
        Assert.NotSame(ResiliencePolicy.Default, ResiliencePolicy.Passthrough);
        Assert.NotEqual(ResiliencePolicy.Default.MaxRetries, ResiliencePolicy.Passthrough.MaxRetries);
    }
}
