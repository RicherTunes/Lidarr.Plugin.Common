using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Common.Services.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Bridge;

[Trait("Category", "Unit")]
public class AuthGatedSearchHelperTests
{
    private static AuthFailureGate NewGate(out DefaultAuthFailureHandler handler)
    {
        handler = new DefaultAuthFailureHandler(NullLogger<DefaultAuthFailureHandler>.Instance, failureThreshold: 1);
        return new AuthFailureGate(handler);
    }

    private static IReadOnlyList<int> Some() => new[] { 1, 2, 3 };

    [Fact]
    public async Task HealthyGate_runsSearch_andReturnsResults()
    {
        var gate = NewGate(out _);
        var called = false;

        var result = await AuthGatedSearchHelper.ExecuteAsync<int>(
            gate,
            _ => { called = true; return Task.FromResult(Some()); });

        Assert.True(called);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task AuthFailure_latchesGate_andReturnsEmpty()
    {
        var gate = NewGate(out _);
        Assert.True(gate.IsHealthy);

        var result = await AuthGatedSearchHelper.ExecuteAsync<int>(
            gate,
            _ => throw new InvalidOperationException("Not authenticated"));

        Assert.Empty(result);
        Assert.False(gate.IsHealthy); // gate latched on the auth failure
    }

    [Fact]
    public async Task ExecutorAllFailedSignal_propagates_andDoesNotLatch()
    {
        // THE codex contract: the all-failed IOE must NOT be swallowed as auth nor latch the gate.
        var gate = NewGate(out _);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AuthGatedSearchHelper.ExecuteAsync<int>(
                gate,
                _ => throw new InvalidOperationException(
                    "All 3 Tidal request(s) failed; surfacing the error instead of an empty result.")));

        Assert.Contains("All 3", ex.Message);
        Assert.True(gate.IsHealthy); // a non-auth failure never latches the gate
    }

    [Fact]
    public async Task GenericHttpFailure_propagates()
    {
        var gate = NewGate(out _);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            AuthGatedSearchHelper.ExecuteAsync<int>(
                gate,
                _ => throw new HttpRequestException("Internal Server Error"),
                statusOf: _ => 500));

        Assert.True(gate.IsHealthy);
    }

    [Fact]
    public async Task Status403ViaExtractor_latchesGate_andReturnsEmpty()
    {
        var gate = NewGate(out _);

        var result = await AuthGatedSearchHelper.ExecuteAsync<int>(
            gate,
            _ => throw new HttpRequestException("forbidden"),
            statusOf: _ => 403);

        Assert.Empty(result);
        Assert.False(gate.IsHealthy);
    }

    [Fact]
    public async Task Cancellation_propagates_andDoesNotLatch()
    {
        var gate = NewGate(out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AuthGatedSearchHelper.ExecuteAsync<int>(
                gate,
                ct => throw new OperationCanceledException(ct),
                cancellationToken: cts.Token));

        Assert.True(gate.IsHealthy);
    }

    [Fact]
    public async Task LatchedGate_shortCircuits_withoutCallingSearch()
    {
        var gate = NewGate(out var handler);
        await handler.HandleFailureAsync(new AuthFailure { ErrorCode = "401", Message = "bad" });
        gate.ShouldShortCircuit(); // consume the single probe slot so the next call hard-short-circuits

        var called = false;
        var result = await AuthGatedSearchHelper.ExecuteAsync<int>(
            gate,
            _ => { called = true; return Task.FromResult(Some()); });

        Assert.False(called);
        Assert.Empty(result);
    }
}
