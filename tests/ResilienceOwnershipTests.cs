using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class ResilienceOwnershipTests
{
    [Fact]
    public async Task Should_ReleaseEachGateOnlyOnce_WhenLeaseIsDisposedAgain()
    {
        using var aggregate = new SemaphoreSlim(1, 1);
        using var profile = new SemaphoreSlim(1, 1);
        var lease = await HostGateLease.AcquireAsync(aggregate, profile, CancellationToken.None);
        Assert.Equal(0, aggregate.CurrentCount);
        Assert.Equal(0, profile.CurrentCount);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, aggregate.CurrentCount);
        Assert.Equal(1, profile.CurrentCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Should_PreferCallerCancellation_WhenBothSourcesCancel(bool callerFirst)
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        using var timeout = new ResilienceTimeout(TimeSpan.FromSeconds(3), caller.Token, clock);
        if (callerFirst) { caller.Cancel(); }
        clock.Advance(TimeSpan.FromSeconds(3));
        if (!callerFirst) { caller.Cancel(); }
        Assert.True(timeout.Token.IsCancellationRequested);
        Assert.False(timeout.IsTimeout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Should_NotScheduleTimeout_WhenDisabledOrInfinite(bool infinite)
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        using var timeout = new ResilienceTimeout(infinite ? Timeout.InfiniteTimeSpan : null, caller.Token, clock);
        clock.Advance(TimeSpan.FromDays(60));
        Assert.False(timeout.Token.IsCancellationRequested);
        caller.Cancel();
        Assert.True(timeout.Token.IsCancellationRequested);
        Assert.False(timeout.IsTimeout);
    }

    [Fact]
    public void Should_DisposeDeadlineTimer_WhenOperationCompletes()
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        var timeout = new ResilienceTimeout(TimeSpan.FromSeconds(3), caller.Token, clock);
        var token = timeout.Token;
        timeout.Dispose();
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void Should_CancelImmediately_WhenTimeoutIsZero()
    {
        using var timeout = new ResilienceTimeout(TimeSpan.Zero, CancellationToken.None, new FakeTimeProvider());
        Assert.True(timeout.IsTimeout);
        Assert.True(timeout.Token.IsCancellationRequested);
    }
}
