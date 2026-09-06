using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Hosting;

[Collection("HostGateRegistryShutdown")]
public sealed class HostGateLifecycleTests : IDisposable
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Should_PreserveInFlightResponse_WhenRegistryIsRetired(int executor, bool shutdown)
    {
        var host = $"gate-retirement-{executor}-{shutdown.ToString().ToLowerInvariant()}.test";
        var entered = NewSignal();
        var finish = NewSignal();
        using var cancellation = new CancellationTokenSource();
        using var expected = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("completed despite registry cleanup")
        };
        async Task<HttpResponseMessage> Send(CancellationToken token)
        {
            entered.TrySetResult(true);
            await finish.Task.WaitAsync(token);
            return expected;
        }
        using var client = new HttpClient(new Handler(Send)) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/resource");
        var pending = Execute(executor, client, request, Send, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (shutdown) HostGateRegistry.Shutdown();
            else HostGateRegistry.Clear(host);
            finish.TrySetResult(true);

            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(expected, response);
            Assert.Equal("completed despite registry cleanup", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            finish.TrySetResult(true);
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) when (pending.IsCompleted) { /* Observe a failed assertion's worker before cleanup. */ }
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Should_DrainWaitersWithoutCreatingASecondHostBudget(int executor, bool shutdown)
    {
        var host = $"gate-drain-{executor}-{shutdown.ToString().ToLowerInvariant()}.test";
        var entered = NewSignal();
        var finish = NewSignal();
        var sends = 0;
        var active = 0;
        var overlaps = 0;
        using var cancellation = new CancellationTokenSource();
        async Task<HttpResponseMessage> Send(CancellationToken token)
        {
            Interlocked.Increment(ref sends);
            if (Interlocked.Increment(ref active) != 1) Interlocked.Increment(ref overlaps);
            entered.TrySetResult(true);
            try
            {
                await finish.Task.WaitAsync(token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        using var client = new HttpClient(new Handler(Send)) { Timeout = Timeout.InfiniteTimeSpan };
        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/first");
        using var queuedRequest = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/queued");
        using var newRequest = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/new");
        var tasks = new System.Collections.Generic.List<Task<HttpResponseMessage>>();
        try
        {
            tasks.Add(Execute(executor, client, firstRequest, Send, cancellation.Token));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(HostGateRegistry.TryGetState(host, out var originalGate));
            tasks.Add(Execute(executor, client, queuedRequest, Send, cancellation.Token));
            Assert.False(tasks[1].IsCompleted);
            if (shutdown) HostGateRegistry.Shutdown();
            else HostGateRegistry.Clear(host);
            Assert.True(HostGateRegistry.TryGetState(host, out var retiringGate));
            Assert.Same(originalGate.Semaphore, retiringGate.Semaphore);

            tasks.Add(Execute(executor, client, newRequest, Send, cancellation.Token));
            Assert.True(HostGateRegistry.TryGetState(host, out var reusedGate));
            Assert.Same(originalGate.Semaphore, reusedGate.Semaphore);
            Assert.Equal(1, Volatile.Read(ref sends));
            finish.TrySetResult(true);
            var responses = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var response in responses)
            {
                using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            Assert.Equal(3, sends);
            Assert.Equal(0, overlaps);
            Assert.False(HostGateRegistry.TryGetState(host, out _));
            Assert.Throws<ObjectDisposedException>(() => originalGate.Semaphore.Wait(0));
        }
        finally
        {
            finish.TrySetResult(true);
            cancellation.Cancel();
            foreach (var task in tasks)
            {
                try { (await task.WaitAsync(TimeSpan.FromSeconds(10))).Dispose(); }
                catch (Exception) when (task.IsCompleted) { }
            }
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void Should_ProtectLookupBeforeWait_AndEventuallyDispose(bool pair, int retirement)
    {
        const string host = "reserved-before-wait.test";
        using var reservation = HostGateRegistry.Reserve(host, 1, pair ? host : null, pair ? 1 : null);
        var profile = reservation.Profile;
        var aggregate = reservation.Aggregate;
        if (retirement == 0) HostGateRegistry.Clear(host);
        else if (retirement == 1) HostGateRegistry.Shutdown();
        else HostGateRegistry.SweepIdle(TimeSpan.Zero);

        Assert.True(HostGateRegistry.TryGetState(host, out var current));
        Assert.Same(profile, current.Semaphore);
        Assert.True(profile.Wait(0));
        profile.Release();
        if (aggregate is not null)
        {
            Assert.True(aggregate.Wait(0));
            aggregate.Release();
        }
        reservation.Dispose();
        reservation.Dispose();
        if (retirement == 2) HostGateRegistry.SweepIdle(TimeSpan.Zero);
        Assert.False(HostGateRegistry.TryGetState(host, out _));
        Assert.Throws<ObjectDisposedException>(() => profile.Wait(0));
        if (aggregate is not null) Assert.Throws<ObjectDisposedException>(() => aggregate.Wait(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ReturnCanceledReservations_WithoutBreakingTheActiveOwner(bool pair)
    {
        const string host = "retired-canceled-waiter.test";
        using var active = pair
            ? await HostGateLease.AcquireAsync(host, host, 1, 1, CancellationToken.None)
            : await HostGateLease.AcquireAsync(host, 1, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var pending = pair
            ? HostGateLease.AcquireAsync(host, host, 1, 1, cancellation.Token)
            : HostGateLease.AcquireAsync(host, 1, cancellation.Token);
        Assert.False(pending.IsCompleted);
        HostGateRegistry.Shutdown();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(HostGateRegistry.TryGetState(host, out var gate));
        Assert.Equal(0, gate.Semaphore.CurrentCount);
        active.Dispose();
        Assert.False(HostGateRegistry.TryGetState(host, out _));
        Assert.Throws<ObjectDisposedException>(() => gate.Semaphore.Wait(0));
    }

    [Fact]
    public async Task Should_ReserveBothGatesWhileBlockedOnProfile_AndRollbackCancellation()
    {
        const string host = "partial-lifecycle.test";
        using var profileOwner = await HostGateLease.AcquireAsync(host, 1, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var pending = HostGateLease.AcquireAsync(host, host, 1, 1, cancellation.Token);
        Assert.False(pending.IsCompleted);
        var aggregate = HostGateRegistry.GetAggregate(host, 1);
        Assert.Equal(0, aggregate.CurrentCount);
        HostGateRegistry.Shutdown();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Throws<ObjectDisposedException>(() => aggregate.Wait(0));
        Assert.True(HostGateRegistry.TryGetState(host, out var gate));
        Assert.Equal(0, gate.Semaphore.CurrentCount);
        profileOwner.Dispose();
        Assert.False(HostGateRegistry.TryGetState(host, out _));
    }

    [Fact]
    public void Should_ReclaimIdleEntriesWithoutEvictingReservedEntries()
    {
        var idle = HostGateRegistry.Get("idle-lifecycle.test", 1);
        using var active = HostGateRegistry.Reserve("reserved-lifecycle.test", 1);
        HostGateRegistry.SweepIdle(TimeSpan.Zero);
        Assert.Throws<ObjectDisposedException>(() => idle.Wait(0));
        Assert.True(HostGateRegistry.TryGetState("reserved-lifecycle.test", out _));
        active.Dispose();
        HostGateRegistry.SweepIdle(TimeSpan.Zero);
        Assert.False(HostGateRegistry.TryGetState("reserved-lifecycle.test", out _));
    }

    [Fact]
    public void Should_RejectInvalidPairBeforeRegisteringEitherGate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HostGateRegistry.Reserve("invalid-pair.test", 1, "invalid-pair.test", 0));
        Assert.False(HostGateRegistry.TryGetState("invalid-pair.test", out _));
    }

    [Fact]
    public async Task Should_KeepOneAggregateBudgetAcrossDistinctProfilesDuringRetirement()
    {
        const string host = "shared-aggregate-retirement.test";
        using var cancellation = new CancellationTokenSource();
        using var first = await HostGateLease.AcquireAsync(host, host + "|first", 1, 1, cancellation.Token);
        var originalAggregate = HostGateRegistry.GetAggregate(host, 1);
        var secondTask = HostGateLease.AcquireAsync(host, host + "|second", 1, 1, cancellation.Token);
        HostGateRegistry.Shutdown();
        var thirdTask = HostGateLease.AcquireAsync(host, host + "|third", 1, 1, cancellation.Token);
        try
        {
            Assert.Same(originalAggregate, HostGateRegistry.GetAggregate(host, 1));
            Assert.False(secondTask.IsCompleted);
            Assert.False(thirdTask.IsCompleted);
            first.Dispose();
            var winner = await Task.WhenAny(secondTask, thirdTask).WaitAsync(TimeSpan.FromSeconds(10));
            var other = ReferenceEquals(winner, secondTask) ? thirdTask : secondTask;
            using var next = await winner;
            Assert.False(other.IsCompleted);
            next.Dispose();
            using var last = await other.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            first.Dispose();
            cancellation.Cancel();
            foreach (var task in new[] { secondTask, thirdTask })
            {
                try { (await task.WaitAsync(TimeSpan.FromSeconds(10))).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
        Assert.Throws<ObjectDisposedException>(() => originalAggregate.Wait(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreserveCapacityDuringConcurrentClearSweepAndShutdown(bool pair)
    {
        const string host = "concurrent-lifecycle-authority.test";
        using var cancellation = new CancellationTokenSource();
        var start = NewSignal();
        var running = 0;
        var completed = 0;
        var violations = 0;
        async Task Worker()
        {
            await start.Task;
            for (var i = 0; i < 50; i++)
            {
                using var lease = pair
                    ? await HostGateLease.AcquireAsync(host, host, 1, 1, cancellation.Token)
                    : await HostGateLease.AcquireAsync(host, 1, cancellation.Token);
                if (Interlocked.Increment(ref running) != 1) Interlocked.Increment(ref violations);
                await Task.Yield();
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref completed);
            }
        }
        async Task Cleanup()
        {
            await start.Task;
            for (var i = 0; i < 200; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                HostGateRegistry.Clear(host);
                HostGateRegistry.SweepIdle(TimeSpan.Zero);
                HostGateRegistry.Shutdown();
                await Task.Yield();
            }
        }
        var all = Task.WhenAll(Task.Run(Worker), Task.Run(Worker), Task.Run(Worker), Task.Run(Worker), Task.Run(Cleanup));
        start.TrySetResult(true);
        try
        {
            await all.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(200, completed);
            Assert.Equal(0, violations);
            Assert.Equal(0, running);
        }
        finally
        {
            cancellation.Cancel();
            try { await all.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) when (all.IsCompleted) { }
        }
        HostGateRegistry.Shutdown();
        Assert.False(HostGateRegistry.TryGetState(host, out _));
        Assert.False(HostGateRegistry.IsSweeperActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreserveLimitGrowthAndDrainExactlyTheOwnedPermits(bool pair)
    {
        const string host = "growing-lifecycle.test";
        using var cancellation = new CancellationTokenSource();
        Task<HostGateLease> Acquire(int limit) => pair
            ? HostGateLease.AcquireAsync(host, host, limit, limit, cancellation.Token)
            : HostGateLease.AcquireAsync(host, limit, cancellation.Token);
        using var first = await Acquire(1);
        var secondTask = Acquire(1);
        Assert.False(secondTask.IsCompleted);
        var thirdTask = Acquire(3);
        HostGateLease? second = null;
        HostGateLease? third = null;
        Task<HostGateLease>? fourthTask = null;
        try
        {
            second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
            third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(HostGateRegistry.TryGetState(host, out var grown));
            Assert.Equal(3, grown.Limit);
            Assert.Equal(0, grown.Semaphore.CurrentCount);
            fourthTask = Acquire(1);
            Assert.False(fourthTask.IsCompleted);
            HostGateRegistry.Shutdown();
            first.Dispose();
            using var fourth = await fourthTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(HostGateRegistry.TryGetState(host, out var retiring));
            Assert.Same(grown.Semaphore, retiring.Semaphore);
            Assert.Equal(3, retiring.Limit);
            Assert.Equal(0, retiring.Semaphore.CurrentCount);
        }
        finally
        {
            first.Dispose();
            second?.Dispose();
            third?.Dispose();
            cancellation.Cancel();
            foreach (var task in new[] { secondTask, thirdTask, fourthTask })
            {
                if (task is null) continue;
                try { (await task.WaitAsync(TimeSpan.FromSeconds(10))).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
        Assert.False(HostGateRegistry.TryGetState(host, out _));
    }

    public void Dispose() => HostGateRegistry.Shutdown();

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<HttpResponseMessage> Execute(int executor, HttpClient client, HttpRequestMessage request,
        Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken token)
    {
        return executor switch
        {
            0 => GenericResilienceExecutor.ExecuteWithResilienceAsync(request, (_, ct) => send(ct),
                r => Task.FromResult(r), r => r.RequestUri!.Host, r => (int)r.StatusCode, _ => TimeSpan.Zero,
                ResiliencePolicy.Default.With(maxRetries: 1, maxConcurrencyPerHost: 1), token),
            1 => client.ExecuteWithResilienceAsync(request, 1, TimeSpan.FromSeconds(30), 1, null, token),
            _ => client.ExecuteWithResilienceAsync(request, 1, TimeSpan.FromSeconds(30), 1, null, TimeProvider.System, token)
        };
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(cancellationToken);
    }
}
