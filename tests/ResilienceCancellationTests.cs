using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class ResilienceCancellationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Should_UseSuppliedClock_AndReportTimeoutDuringSendOrBackoff(bool typedHttp, bool backoff)
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://timeout-{typedHttp}-{backoff}.test/resource");
        var sentToken = CancellationToken.None;
        var sends = 0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage _, CancellationToken token)
        {
            sentToken = token;
            sends++;
            if (!backoff)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return response;
        }
        using var client = new HttpClient(new Handler(Send));
        var pending = Execute(typedHttp, client, request, Send, clock, TimeSpan.FromSeconds(3), caller.Token);
        try
        {
            Assert.Equal(1, sends);
            Assert.False(sentToken.IsCancellationRequested);
            clock.Advance(TimeSpan.FromSeconds(3));
            // A deterministic assertion, not a watchdog timeout mistaken for the executor's error.
            // HttpClient disposes its internal linked token once SendAsync returns;
            // that token no longer reflects a later timeout while the executor backs off.
            if (!typedHttp || !backoff)
            {
                Assert.True(sentToken.IsCancellationRequested, "The supplied clock must drive the configured operation timeout.");
            }
            var error = await Assert.ThrowsAsync<TimeoutException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            Assert.Equal(1, sends);
        }
        finally
        {
            caller.Cancel();
            try { (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) when (pending.IsCompleted) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreserveCallerCancellationDuringBackoff(bool typedHttp)
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://caller-backoff-{typedHttp}.test/resource");
        var sends = 0;
        Task<HttpResponseMessage> Send(HttpRequestMessage _, CancellationToken __)
        {
            sends++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return Task.FromResult(response);
        }
        using var client = new HttpClient(new Handler(Send));
        var pending = Execute(typedHttp, client, request, Send, clock, TimeSpan.FromSeconds(30), caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ReportTimeoutWhileWaitingForProfile_WithoutReleasingUnownedPermit(bool typedHttp)
    {
        var host = $"timeout-profile-{typedHttp}.test".ToLowerInvariant();
        var gate = HostGateRegistry.Get(host, 1);
        var aggregate = HostGateRegistry.GetAggregate(host, 1);
        await gate.WaitAsync();
        using var caller = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/resource");
        var clock = new FakeTimeProvider();
        var sends = 0;
        Task<HttpResponseMessage> Send(HttpRequestMessage _, CancellationToken __)
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
        using var client = new HttpClient(new Handler(Send));
        var pending = Execute(typedHttp, client, request, Send, clock, TimeSpan.FromSeconds(2), caller.Token);
        try
        {
            Assert.False(pending.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(2));
            var error = await Assert.ThrowsAsync<TimeoutException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            Assert.Equal(0, gate.CurrentCount);
            Assert.Equal(1, aggregate.CurrentCount);
            Assert.Equal(0, sends);
        }
        finally
        {
            caller.Cancel();
            try { (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) when (pending.IsCompleted) { }
            gate.Release();
            HostGateRegistry.Clear(host);
        }
    }

    [Fact]
    public async Task Should_NotSendAfterCallerCancellationDuringClone()
    {
        using var caller = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cancel-clone.test/resource");
        var sends = 0;
        var pending = GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
            request,
            (_, _) =>
            {
                sends++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
            r => { caller.Cancel(); return Task.FromResult(r); },
            r => r.RequestUri?.Host,
            r => (int)r.StatusCode,
            _ => TimeSpan.Zero,
            ResiliencePolicy.Default,
            caller.Token);
        var error = await Record.ExceptionAsync(async () => { using var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); });
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task Should_NotRetryAfterCallerCancellationWithZeroBackoff()
    {
        using var caller = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cancel-zero-delay.test/resource");
        var sends = 0;
        var pending = GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
            request,
            (_, _) => Task.FromResult(new HttpResponseMessage(++sends == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)),
            r => Task.FromResult(r), r => r.RequestUri?.Host, r => (int)r.StatusCode,
            _ => { caller.Cancel(); return TimeSpan.Zero; },
            ResiliencePolicy.Default.With(maxRetries: 2),
            caller.Token);
        var error = await Record.ExceptionAsync(async () => { using var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); });
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(1, sends);
    }

    private static Task<HttpResponseMessage> Execute(bool typedHttp, HttpClient client, HttpRequestMessage request,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeProvider clock,
        TimeSpan timeout, CancellationToken caller)
    {
        var policy = ResiliencePolicy.Default.With(maxRetries: 2, retryBudget: TimeSpan.FromSeconds(60), maxConcurrencyPerHost: 1, perRequestTimeout: timeout);
        return typedHttp
            ? client.ExecuteWithResilienceAsync(request, 2, policy.RetryBudget, 1, timeout, clock, caller)
            : GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(request, send,
                r => Task.FromResult(r), r => r.RequestUri?.Host, r => (int)r.StatusCode,
                r => r.Headers.RetryAfter?.Delta, policy, clock, caller);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
