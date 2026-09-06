using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class ResilienceRetryWaitTests
{
    private static readonly TimeSpan NativeLimit = TimeSpan.FromMilliseconds(4294967294L);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_NotSendAgain_WhenRetryWaitOversleepsBudget(bool typed)
    {
        using var run = new RetryRun(typed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        try
        {
            var timer = await run.Clock.NextTimerAsync(run.Pending);
            Assert.Equal(1, run.Body.DisposeCount);
            timer.FireAfter(TimeSpan.FromSeconds(6));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => run.Pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("retry budget", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(error.InnerException); // not the test watchdog or an operation-timeout translation
            Assert.Equal(1, run.Sends);
            run.AssertReleased();
        }
        finally { await run.StopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_DisposeTimerBeforeRetryWhenCallbackRunsOnPoolThread(bool typed)
    {
        using var run = new RetryRun(typed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        try
        {
            var timer = await run.Clock.NextTimerAsync(run.Pending);
            await Task.Run(() => timer.FireAfter(TimeSpan.FromSeconds(2)));
            using var response = await run.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, run.Sends);
            run.AssertReleased();
        }
        finally { await run.StopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreserveAdmittedRetryAtExactBudgetBoundary(bool typed)
    {
        using var run = new RetryRun(typed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        try
        {
            var timer = await run.Clock.NextTimerAsync(run.Pending);
            timer.FireAfter(TimeSpan.FromSeconds(5));
            using var response = await run.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, run.Sends);
            run.AssertReleased();
        }
        finally { await run.StopAsync(); }
    }

    [Theory]
    [InlineData(false, 1L)]
    [InlineData(true, 1L)]
    [InlineData(false, 10000L)]
    [InlineData(true, 10000L)]
    public async Task Should_SplitOversizedRetryWithoutSendingBeforeTheWholeDelay(bool typed, long extraTicks)
    {
        using var run = new RetryRun(typed, NativeLimit + TimeSpan.FromTicks(extraTicks), TimeSpan.MaxValue);
        try
        {
            var first = await run.Clock.NextTimerAsync(run.Pending);
            Assert.Equal(NativeLimit, first.DueTime);
            first.FireAfter(NativeLimit);
            var second = await run.Clock.NextTimerAsync(run.Pending);
            Assert.Equal(TimeSpan.FromMilliseconds(1), second.DueTime);
            Assert.Equal(1, run.Sends);
            Assert.Equal(1, run.Body.DisposeCount);
            Assert.Equal(1, run.Clock.ActiveTimers);
            second.FireAfter(TimeSpan.FromMilliseconds(1));
            using var response = await run.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, run.Sends);
            Assert.Equal(2, run.Clock.CreatedTimers);
            run.AssertReleased();
        }
        finally { await run.StopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_CancelExtremeRetryWithOnlyOneOutstandingTimer(bool typed)
    {
        // Typed headers cap delta-seconds at int.MaxValue; the generic delegate
        // accepts a full TimeSpan. Exercise the actual valid range of each API.
        var delay = typed ? TimeSpan.FromSeconds(int.MaxValue) : TimeSpan.MaxValue;
        using var run = new RetryRun(typed, delay, TimeSpan.MaxValue);
        try
        {
            var timer = await run.Clock.NextTimerAsync(run.Pending);
            Assert.Equal(NativeLimit, timer.DueTime);
            Assert.Equal(1, run.Clock.CreatedTimers);
            run.Cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, run.Sends);
            run.AssertReleased();
        }
        finally { await run.StopAsync(); }
    }

    [Theory]
    [InlineData(false, 20000000L, 10000000L, 10000000L)]
    [InlineData(true, 20000000L, 10000000L, 10000000L)]
    [InlineData(false, 1L, 0L, 10000L)]
    [InlineData(true, 1L, 0L, 10000L)]
    public async Task Should_RecheckElapsedDelayAfterAnEarlyTimerSignal(
        bool typed, long requestedTicks, long firstElapsedTicks, long remainderTicks)
    {
        using var run = new RetryRun(typed, TimeSpan.FromTicks(requestedTicks), TimeSpan.FromSeconds(10));
        try
        {
            var first = await run.Clock.NextTimerAsync(run.Pending);
            Assert.True(first.DueTime >= TimeSpan.FromMilliseconds(1));
            first.FireAfter(TimeSpan.FromTicks(firstElapsedTicks));
            var second = await run.Clock.NextTimerAsync(run.Pending);
            Assert.Equal(TimeSpan.FromTicks(remainderTicks), second.DueTime);
            Assert.Equal(1, run.Sends);
            second.FireAfter(TimeSpan.FromTicks(remainderTicks));
            using var response = await run.Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, run.Sends);
            run.AssertReleased();
        }
        finally { await run.StopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PrioritizeCallerCancellationAfterBudgetOversleep(bool typed)
    {
        using var run = new RetryRun(typed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        try
        {
            var timer = await run.Clock.NextTimerAsync(run.Pending);
            run.Clock.Advance(TimeSpan.FromSeconds(6));
            run.Cancellation.Cancel();
            timer.FireAfter(TimeSpan.Zero);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, run.Sends);
            run.AssertReleased();
        }
        finally { await run.StopAsync(); }
    }

    [Fact]
    public async Task Should_KeepDefaultTypedClockLongRetryCancellable()
    {
        using var cancellation = new CancellationTokenSource();
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(50));
        var sends = 0;
        using var client = new HttpClient(new Handler(() => { sends++; return response; }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://native-long-retry.test/resource");
        var pending = client.ExecuteWithResilienceAsync(request, 2, TimeSpan.FromDays(60), 1, null, cancellation.Token);
        try
        {
            Assert.False(pending.IsCompleted, "A valid long retry must not fault at native timer creation.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, sends);
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) when (pending.IsCompleted) { }
            HostGateRegistry.Clear(request.RequestUri!.Host);
        }
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public async Task Should_RecheckBudgetAfterCloningTheRetry(int cloneSeconds, bool expired)
    {
        var clock = new ManualClock();
        var host = "retry-clone-" + Guid.NewGuid().ToString("N") + ".test";
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + host);
        var body = new TrackedBody();
        var sends = 0;
        var clones = 0;
        var pending = GenericResilienceExecutor.ExecuteWithResilienceAsync(
            request,
            (_, _) => Task.FromResult(++sends == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = body }
                : new HttpResponseMessage(HttpStatusCode.OK)),
            r =>
            {
                if (++clones == 2) clock.Advance(TimeSpan.FromSeconds(cloneSeconds));
                return Task.FromResult(r);
            },
            r => r.RequestUri!.Host, r => (int)r.StatusCode, _ => TimeSpan.Zero,
            ResiliencePolicy.Default.With(maxRetries: 2, retryBudget: TimeSpan.FromSeconds(5), maxConcurrencyPerHost: 1), clock);
        try
        {
            if (expired)
            {
                var error = await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Contains("retry budget", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            Assert.Equal(expired ? 1 : 2, sends);
            Assert.Equal(2, clones);
            Assert.Equal(1, body.DisposeCount);
            Assert.Equal(1, HostGateRegistry.Get(host, 1).CurrentCount);
        }
        finally { HostGateRegistry.Clear(host); }
    }

    private sealed class RetryRun : IDisposable
    {
        private readonly string _host = "retry-wait-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly HttpRequestMessage _request;
        private readonly HttpClient _client;
        private readonly bool _typed;
        public readonly ManualClock Clock = new();
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TrackedBody Body = new();
        public readonly Task<HttpResponseMessage> Pending;
        public int Sends;

        public RetryRun(bool typed, TimeSpan delay, TimeSpan budget)
        {
            _typed = typed;
            _request = new HttpRequestMessage(HttpMethod.Get, "https://" + _host + "/resource");
            HttpResponseMessage Send()
            {
                if (Interlocked.Increment(ref Sends) > 1)
                {
                    Assert.Equal(0, Clock.ActiveTimers);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = Body };
                if (typed) response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
                return response;
            }
            _client = new HttpClient(new Handler(Send));
            Pending = typed
                ? _client.ExecuteWithResilienceAsync(_request, 2, budget, 1, null, Clock, Cancellation.Token)
                : GenericResilienceExecutor.ExecuteWithResilienceAsync(
                    _request, (_, _) => Task.FromResult(Send()), r => Task.FromResult(r),
                    r => r.RequestUri!.Host, r => (int)r.StatusCode, _ => delay,
                    ResiliencePolicy.Default.With(maxRetries: 2, retryBudget: budget, maxConcurrencyPerHost: 1),
                    Clock, Cancellation.Token);
        }

        public void AssertReleased()
        {
            Assert.Equal(0, Clock.ActiveTimers);
            Assert.Equal(1, Body.DisposeCount);
            Assert.Equal(1, HostGateRegistry.Get(_host, 1).CurrentCount);
            if (_typed) Assert.Equal(1, HostGateRegistry.GetAggregate(_host, 1).CurrentCount);
        }

        public async Task StopAsync()
        {
            Cancellation.Cancel();
            try { (await Pending.WaitAsync(TimeSpan.FromSeconds(10))).Dispose(); }
            catch (Exception) when (Pending.IsCompleted) { } // primary assertions report any fault; observe cleanup
        }

        public void Dispose()
        {
            _request.Dispose();
            _client.Dispose();
            Cancellation.Dispose();
            HostGateRegistry.Clear(_host);
        }
    }

    private sealed class TrackedBody : StringContent
    {
        public int DisposeCount;
        public TrackedBody() : base("original retry response") { }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref DisposeCount);
            base.Dispose(disposing);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _send;
        public Handler(Func<HttpResponseMessage> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_send());
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly Channel<ManualTimer> _timers = Channel.CreateUnbounded<ManualTimer>();
        private long _timestamp;
        public int CreatedTimers;
        public int ActiveTimers;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Independently model the native timer limit; do not use the production helper constant.
            if (dueTime < TimeSpan.Zero || dueTime > NativeLimit) throw new ArgumentOutOfRangeException(nameof(dueTime));
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ManualTimer(this, callback, state, dueTime);
            Interlocked.Increment(ref CreatedTimers);
            Interlocked.Increment(ref ActiveTimers);
            _timers.Writer.TryWrite(timer);
            return timer;
        }

        public async Task<ManualTimer> NextTimerAsync(Task operation)
        {
            var next = _timers.Reader.ReadAsync().AsTask();
            await Task.WhenAny(next, operation).WaitAsync(TimeSpan.FromSeconds(10));
            if (!next.IsCompleted)
            {
                await operation; // expose creation faults instead of masking them as a watchdog timeout
                Assert.True(next.IsCompleted, "The operation sent again before the requested delay elapsed.");
            }
            return await next;
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private int _disposed;
        public TimeSpan DueTime { get; }
        public ManualTimer(ManualClock clock, TimerCallback callback, object? state, TimeSpan dueTime)
            => (_clock, _callback, _state, DueTime) = (clock, callback, state, dueTime);
        public void FireAfter(TimeSpan elapsed)
        {
            _clock.Advance(elapsed);
            if (Volatile.Read(ref _disposed) == 0) _callback(_state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException("One-shot test timer.");
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _clock.ActiveTimers);
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
