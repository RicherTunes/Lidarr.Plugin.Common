using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.Observability;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Observability;

/// <summary>
/// Tests for the Phase 5e dimensional metrics surface (IMetricsRecorder, NullMetricsRecorder,
/// ObservableMetricsRecorder).
/// </summary>
[Trait("Category", "Unit")]
public class MetricsRecorderTests
{
    [Fact]
    public void NullMetricsRecorder_AllOperationsAreNoOp()
    {
        var r = NullMetricsRecorder.Instance;
        // Should not throw or allocate observable state.
        r.Increment("counter");
        r.Increment("counter", 42);
        r.Increment("counter", 1, new Dictionary<string, string> { ["k"] = "v" });
        r.Gauge("gauge", 3.14);
        r.Histogram("hist", 99);
    }

    [Fact]
    public void ObservableMetricsRecorder_PublishesIncrementToSubscribers()
    {
        var r = new ObservableMetricsRecorder();
        var events = new List<MetricEvent>();
        using var sub = r.Subscribe(new TestObserver(events));

        r.Increment("requests", 1, new Dictionary<string, string> { ["provider"] = "anthropic" });

        var ev = Assert.Single(events);
        Assert.Equal("requests", ev.Name);
        Assert.Equal(1.0, ev.Value);
        Assert.Equal(MetricKind.Counter, ev.Kind);
        Assert.NotNull(ev.Tags);
        Assert.Equal("anthropic", ev.Tags!["provider"]);
    }

    [Fact]
    public void ObservableMetricsRecorder_PublishesGaugeAndHistogram()
    {
        var r = new ObservableMetricsRecorder();
        var events = new List<MetricEvent>();
        using var sub = r.Subscribe(new TestObserver(events));

        r.Gauge("queue.depth", 17);
        r.Histogram("latency.ms", 250);

        Assert.Equal(2, events.Count);
        Assert.Equal(MetricKind.Gauge, events[0].Kind);
        Assert.Equal(17.0, events[0].Value);
        Assert.Equal(MetricKind.Histogram, events[1].Kind);
        Assert.Equal(250.0, events[1].Value);
    }

    [Fact]
    public void ObservableMetricsRecorder_DisposingSubscription_StopsDelivery()
    {
        var r = new ObservableMetricsRecorder();
        var events = new List<MetricEvent>();
        var sub = r.Subscribe(new TestObserver(events));

        r.Increment("first");
        sub.Dispose();
        r.Increment("second");

        var ev = Assert.Single(events);
        Assert.Equal("first", ev.Name);
    }

    [Fact]
    public void ObservableMetricsRecorder_MultipleSubscribers_AllReceiveSameEvent()
    {
        var r = new ObservableMetricsRecorder();
        var a = new List<MetricEvent>();
        var b = new List<MetricEvent>();
        using var subA = r.Subscribe(new TestObserver(a));
        using var subB = r.Subscribe(new TestObserver(b));

        r.Increment("shared", 5);

        Assert.Single(a);
        Assert.Single(b);
        Assert.Equal(a[0], b[0]);
    }

    [Fact]
    public void ObservableMetricsRecorder_ThrowingSubscriber_DoesNotBreakOthers()
    {
        var r = new ObservableMetricsRecorder();
        var captured = new List<MetricEvent>();
        using var subBad = r.Subscribe(new ThrowingObserver());
        using var subGood = r.Subscribe(new TestObserver(captured));

        r.Increment("ok"); // bad observer throws; good observer should still receive

        var ev = Assert.Single(captured);
        Assert.Equal("ok", ev.Name);
    }

    [Fact]
    public void NullObserver_Throws()
    {
        var r = new ObservableMetricsRecorder();
        Assert.Throws<ArgumentNullException>(() => r.Subscribe(null!));
    }

    private sealed class TestObserver : IObserver<MetricEvent>
    {
        private readonly List<MetricEvent> _sink;
        public TestObserver(List<MetricEvent> sink) { _sink = sink; }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(MetricEvent value) => _sink.Add(value);
    }

    private sealed class ThrowingObserver : IObserver<MetricEvent>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(MetricEvent value) => throw new InvalidOperationException("boom");
    }
}
