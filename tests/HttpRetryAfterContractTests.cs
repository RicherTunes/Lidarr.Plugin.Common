using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class HttpRetryAfterContractTests
{
    [Theory]
    [InlineData("missing", 429)]
    [InlineData("missing", 503)]
    [InlineData("malformed", 429)]
    [InlineData("malformed", 503)]
    [InlineData("negative-wire", 429)]
    [InlineData("negative-wire", 503)]
    [InlineData("past-date", 429)]
    [InlineData("past-date", 503)]
    [InlineData("at-date", 429)]
    [InlineData("at-date", 503)]
    [InlineData("zero", 429)]
    [InlineData("zero", 503)]
    [InlineData("negative-typed", 429)]
    [InlineData("negative-typed", 503)]
    [InlineData("delta", 429)]
    [InlineData("delta", 503)]
    [InlineData("future-date", 429)]
    [InlineData("future-date", 503)]
    public async Task Should_PreserveHeaderVersusPolicyFallback_WithSuppliedClock(string form, int status)
    {
        var clock = new RecordingClock();
        var now = clock.UtcNow;
        var host = $"header-contract-{form}-{status}.test";
        using var originalContent = new TrackedContent("first response");
        using var original = new HttpResponseMessage((HttpStatusCode)status) { Content = originalContent };
        switch (form)
        {
            case "missing": break;
            case "malformed": Assert.True(original.Headers.TryAddWithoutValidation("Retry-After", "not-a-delay")); break;
            case "negative-wire": Assert.True(original.Headers.TryAddWithoutValidation("Retry-After", "-1")); break;
            case "past-date": original.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-1)); break;
            case "at-date": original.Headers.RetryAfter = new RetryConditionHeaderValue(now); break;
            case "zero": original.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); break;
            case "negative-typed": original.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(-1)); break;
            case "delta": original.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3)); break;
            case "future-date": original.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(3)); break;
            default: throw new ArgumentOutOfRangeException(nameof(form));
        }

        var sends = 0;
        using var handler = new Handler(_ =>
        {
            if (Interlocked.Increment(ref sends) == 1) return original;
            Assert.Equal(1, originalContent.DisposeCount);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("success") };
        });
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/item");
        using var cancellation = new CancellationTokenSource();
        var pending = client.ExecuteWithResilienceAsync(request, 2, TimeSpan.FromMinutes(1), 1, null, clock, cancellation.Token);
        try
        {
            var immediate = form is "zero" or "negative-typed";
            if (immediate)
            {
                Assert.Empty(clock.Delays);
            }
            else
            {
                var scheduled = Assert.Single(clock.Delays);
                if (form is "delta" or "future-date") Assert.Equal(TimeSpan.FromSeconds(3), scheduled);
                else Assert.InRange(scheduled, TimeSpan.FromMilliseconds(2050), TimeSpan.FromMilliseconds(2249));
                Assert.Equal(1, sends);
                Assert.False(pending.IsCompleted);
                clock.Advance(scheduled - TimeSpan.FromTicks(1));
                Assert.Equal(1, sends);
                Assert.False(pending.IsCompleted);
                clock.Advance(TimeSpan.FromTicks(1));
            }

            using var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.Equal("success", await result.Content.ReadAsStringAsync());
            Assert.Equal(2, sends);
            Assert.Equal(1, originalContent.DisposeCount);
            Assert.Equal(form.EndsWith("date", StringComparison.Ordinal) ? 1 : 0, clock.UtcReads);
        }
        finally
        {
            cancellation.Cancel();
            try { using var abandoned = await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            HostGateRegistry.Clear(host);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ReportZeroDelay_NotNegativeDelay_ForNegativeTypedHeader(bool suppliedClock)
    {
        var host = suppliedClock ? "negative-header-trace-supplied.test" : "negative-header-trace-system.test";
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Lidarr.Plugin.Common",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "http.send" && Equals(activity.GetTagItem("net.peer.name"), host))
                    activities.Enqueue(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var sends = 0;
        using var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(++sends == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (sends == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(-1500));
            return response;
        });
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/item");
        try
        {
            using var response = suppliedClock
                ? await client.ExecuteWithResilienceAsync(request, 2, TimeSpan.FromSeconds(20), 1, null, new RecordingClock(), CancellationToken.None)
                : await client.ExecuteWithResilienceAsync(request, 2, TimeSpan.FromSeconds(20), 1, null, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, sends);
            var retry = Assert.Single(activities.SelectMany(activity => activity.Events), item => item.Name == "retry");
            var delay = Assert.Single(retry.Tags, tag => tag.Key == "retry.delay.ms");
            Assert.Equal(0L, Assert.IsType<long>(delay.Value));
        }
        finally { HostGateRegistry.Clear(host); }
    }

    [Theory]
    [InlineData(-15000000L)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(15000000L)]
    public void Should_NormalizeDeltaWithoutReadingUtc(long ticks)
    {
        var clock = new HeaderClock(DateTimeOffset.MinValue, failOnRead: true);
        var result = RateLimitHeaderUtilities.ResolveRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromTicks(ticks)), clock);
        Assert.Equal(TimeSpan.FromTicks(Math.Max(0, ticks)), result);
        Assert.Equal(0, clock.Reads);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(12345678L)]
    public void Should_ResolveDateFromExactlyOneSuppliedUtcReading(long differenceTicks)
    {
        var now = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.FromHours(5));
        var clock = new HeaderClock(now);
        var result = RateLimitHeaderUtilities.ResolveRetryAfter(new RetryConditionHeaderValue(now.AddTicks(differenceTicks).ToUniversalTime()), clock);
        Assert.Equal(TimeSpan.FromTicks(Math.Max(0, differenceTicks)), result);
        Assert.Equal(1, clock.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Should_ResolveExtremeDatesWithoutCalendarDeadlineArithmetic(bool future)
    {
        var now = future ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue;
        var target = future ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;
        var clock = new HeaderClock(now);
        var result = RateLimitHeaderUtilities.ResolveRetryAfter(new RetryConditionHeaderValue(target), clock);
        Assert.Equal(future ? DateTimeOffset.MaxValue - DateTimeOffset.MinValue : TimeSpan.Zero, result);
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public void Should_NotConsultUtcForMissingHeader()
    {
        var clock = new HeaderClock(DateTimeOffset.MinValue, failOnRead: true);
        Assert.Equal(TimeSpan.Zero, RateLimitHeaderUtilities.ResolveRetryAfter(null, clock));
        Assert.Equal(0, clock.Reads);
    }

    [Fact]
    public void Should_RejectMissingClockInInternalResolution()
    {
        var error = Assert.Throws<ArgumentNullException>(() => RateLimitHeaderUtilities.ResolveRetryAfter(null, null!));
        Assert.Equal("timeProvider", error.ParamName);
    }

    [Theory]
    [InlineData("missing", 1, 2050, 2249)]
    [InlineData("missing", 3, 8050, 8249)]
    [InlineData("missing", int.MaxValue, 30050, 30249)]
    [InlineData("null-response", 1, 2050, 2249)]
    [InlineData("malformed", 1, 2050, 2249)]
    [InlineData("past-date", 1, 2050, 2249)]
    [InlineData("zero", 1, 50, 249)]
    [InlineData("negative-typed", 1, 50, 249)]
    [InlineData("delta", 1, 3050, 3249)]
    public void Should_PreserveDownloadBackoffAndJitter_WhileNormalizingHeaderDelay(string form, int attempt, int minimumMs, int maximumMs)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        switch (form)
        {
            case "missing": case "null-response": break;
            case "malformed": Assert.True(response.Headers.TryAddWithoutValidation("Retry-After", "not-a-delay")); break;
            case "past-date": response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.MinValue); break;
            case "zero": response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); break;
            case "negative-typed": response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(-1500)); break;
            case "delta": response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3)); break;
            default: throw new ArgumentOutOfRangeException(nameof(form));
        }
        using var client = new TestDownloadClient(new TestSettings(), null);
        var delay = client.InvokeRetryDelay(attempt, form == "null-response" ? null : response);
        Assert.InRange(delay, TimeSpan.FromMilliseconds(minimumMs), TimeSpan.FromMilliseconds(maximumMs));
    }

    [Theory]
    [InlineData("delta", 429, 3)]
    [InlineData("future-date", 429, 3)]
    [InlineData("past-date", 503, 0)]
    [InlineData("negative-typed", 503, 0)]
    [InlineData("zero", 429, 0)]
    [InlineData("missing", 429, -1)]
    [InlineData("malformed", 429, -1)]
    [InlineData("delta", 200, -1)]
    public async Task Should_ReportTelemetryUsingOneCapturedInstant_WithoutOwningResponse(string form, int status, int expectedSeconds)
    {
        var now = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var clock = new HeaderClock(now);
        var observer = new CapturingObserver();
        using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("caller response") };
        switch (form)
        {
            case "missing": break;
            case "malformed": Assert.True(response.Headers.TryAddWithoutValidation("Retry-After", "not-a-delay")); break;
            case "delta": response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3)); break;
            case "future-date": response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(3)); break;
            case "past-date": response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-3)); break;
            case "negative-typed": response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(-3)); break;
            case "zero": response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); break;
            default: throw new ArgumentOutOfRangeException(nameof(form));
        }
        using var inner = new Handler(_ => response);
        using var telemetry = new RateLimitTelemetryHandler(observer, clock, inner);
        using var client = new HttpClient(telemetry);
        using var result = await client.GetAsync("https://telemetry-header-contract.test/item");
        Assert.Same(response, result);
        Assert.Equal("caller response", await result.Content.ReadAsStringAsync());
        if (expectedSeconds < 0)
        {
            Assert.Empty(observer.Observations);
            Assert.Equal(0, clock.Reads);
        }
        else
        {
            var observation = Assert.Single(observer.Observations);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), observation.Delay);
            Assert.Equal(now, observation.Now);
            Assert.Equal(1, clock.Reads);
        }
    }

    private sealed class CapturingObserver : IRateLimitObserver
    {
        public ConcurrentQueue<(TimeSpan Delay, DateTimeOffset Now)> Observations { get; } = new();
        public void RecordRetryAfter(TimeSpan delay, DateTimeOffset nowUtc) => Observations.Enqueue((delay, nowUtc));
    }

    private sealed class HeaderClock : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        private readonly bool _failOnRead;
        public HeaderClock(DateTimeOffset utcNow, bool failOnRead = false) { _utcNow = utcNow; _failOnRead = failOnRead; }
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            if (_failOnRead) throw new InvalidOperationException("Unexpected UTC dependency for a duration.");
            return _utcNow;
        }
    }

    private sealed class RecordingClock : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));
        private int _utcReads;
        public ConcurrentQueue<TimeSpan> Delays { get; } = new();
        public int UtcReads => Volatile.Read(ref _utcReads);
        public DateTimeOffset UtcNow => _inner.GetUtcNow();
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref _utcReads);
            return _inner.GetUtcNow();
        }
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Enqueue(dueTime);
            return _inner.CreateTimer(callback, state, dueTime, period);
        }
        public void Advance(TimeSpan duration) => _inner.Advance(duration);
    }

    private sealed class TrackedContent : StringContent
    {
        public TrackedContent(string value) : base(value) { }
        public int DisposeCount { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_send(request));
    }
}
