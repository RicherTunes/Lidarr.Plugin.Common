using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class ResilienceJitterBoundaryTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(9999L)]
    [InlineData(10001L)]
    [InlineData(21474836470000L)]
    [InlineData(21474836480000L)]
    [InlineData(long.MaxValue)]
    public void Should_PreserveFixedJitterExactly(long ticks)
    {
        var fixedDelay = TimeSpan.FromTicks(ticks);
        var policy = ResiliencePolicy.Default.With(jitterMin: fixedDelay, jitterMax: fixedDelay);

        Assert.Equal(ticks, policy.ComputeJitter().Ticks);
    }

    [Theory]
    [InlineData(1L, 9L)]
    [InlineData(9001L, 19001L)]
    [InlineData(21474836460000L, 21474836470000L)]
    [InlineData(21474836480000L, 21474836500000L)]
    [InlineData(0L, long.MaxValue)]
    [InlineData(long.MaxValue - 1, long.MaxValue)]
    public void Should_KeepEverySampleWithinTheConfiguredInterval(long minimumTicks, long maximumTicks)
    {
        var policy = ResiliencePolicy.Default.With(
            jitterMin: TimeSpan.FromTicks(minimumTicks),
            jitterMax: TimeSpan.FromTicks(maximumTicks));

        // Assert bounds only: no probabilistic requirements about which values must appear.
        for (var i = 0; i < 64; i++)
        {
            Assert.InRange(policy.ComputeJitter().Ticks, minimumTicks, maximumTicks);
        }
    }

    [Fact]
    public void Should_PreserveWholeMillisecondSamplingForTheDefaultPolicy()
    {
        for (var i = 0; i < 64; i++)
        {
            var ticks = ResiliencePolicy.Default.ComputeJitter().Ticks;
            Assert.InRange(ticks, 500000L, 2500000L);
            Assert.Equal(0L, ticks % TimeSpan.TicksPerMillisecond);
        }
    }

    [Theory]
    [InlineData(long.MaxValue, 10000L)]
    [InlineData(long.MaxValue - 9999L, 10000L)]
    public async Task Should_ReturnTheUsableResponse_WhenBackoffPlusJitterCannotFitAnyBudget(
        long backoffTicks, long jitterTicks)
    {
        var clock = new FakeTimeProvider();
        var backoff = TimeSpan.FromTicks(backoffTicks);
        var jitter = TimeSpan.FromTicks(jitterTicks);
        var policy = ResiliencePolicy.Default.With(
            maxRetries: 2, retryBudget: TimeSpan.MaxValue,
            initialBackoff: backoff, maxBackoff: backoff,
            jitterMin: jitter, jitterMax: jitter);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://jitter-sum-overflow.test/resource");
        using var original = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("retry budget exhausted")
        };
        var sends = 0;

        using var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
            request,
            (_, _) => { sends++; return Task.FromResult(original); },
            r => Task.FromResult(r), r => r.RequestUri?.Host,
            r => (int)r.StatusCode, _ => null,
            policy, clock).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, sends);
        Assert.Same(original, response);
        Assert.Equal("retry budget exhausted", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(10000L, 10000L, true, 20000L)]
    [InlineData(1L, 1L, true, 2L)]
    [InlineData(long.MaxValue, 0L, true, long.MaxValue)]
    [InlineData(long.MaxValue - 1, 1L, true, long.MaxValue)]
    [InlineData(long.MaxValue, 1L, false, 0L)]
    [InlineData(1L, long.MaxValue, false, 0L)]
    public void Should_CombineOnlyRepresentableDelays(
        long backoffTicks, long jitterTicks, bool expectedFits, long expectedDelayTicks)
    {
        var backoff = TimeSpan.FromTicks(backoffTicks);
        var jitter = TimeSpan.FromTicks(jitterTicks);
        var policy = ResiliencePolicy.Default.With(
            initialBackoff: backoff, maxBackoff: backoff,
            jitterMin: jitter, jitterMax: jitter);

        var fits = policy.TryComputeRetryDelay(1, out var delay);

        Assert.Equal(expectedFits, fits);
        Assert.Equal(expectedDelayTicks, delay.Ticks);
    }

    [Fact]
    public async Task Should_UseServerDelayWithoutEvaluatingAnOverflowingPolicyFallback()
    {
        var clock = new FakeTimeProvider();
        var policy = ResiliencePolicy.Default.With(
            maxRetries: 2, retryBudget: TimeSpan.FromSeconds(5),
            initialBackoff: TimeSpan.MaxValue, maxBackoff: TimeSpan.MaxValue,
            jitterMin: TimeSpan.MaxValue, jitterMax: TimeSpan.MaxValue);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://jitter-server-precedence.test/resource");
        using var original = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        using var success = new HttpResponseMessage(HttpStatusCode.OK);
        var sends = 0;

        using var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
            request,
            (_, _) => Task.FromResult(++sends == 1 ? original : success),
            r => Task.FromResult(r), r => r.RequestUri?.Host,
            r => (int)r.StatusCode, _ => TimeSpan.Zero,
            policy, clock).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, sends);
        Assert.Same(success, response);
    }
}
