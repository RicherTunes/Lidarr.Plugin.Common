using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Observability;

/// <summary>
/// Dimensional metric event surfaced by an <see cref="IMetricsRecorder"/>. Carries a name, a value, the
/// metric kind, optional tags, and a timestamp. Plugins that wire to Prometheus/StatsD/etc. can subscribe
/// to <see cref="ObservableMetricsRecorder"/> and translate <see cref="MetricEvent"/> into the host
/// metrics library's native shape without forcing common to take a dependency on any metrics package.
/// </summary>
public sealed record MetricEvent(
    string Name,
    double Value,
    MetricKind Kind,
    IReadOnlyDictionary<string, string>? Tags,
    DateTimeOffset Timestamp);

/// <summary>The category of metric represented by a <see cref="MetricEvent"/>.</summary>
public enum MetricKind
{
    /// <summary>Cumulative counter; <see cref="MetricEvent.Value"/> is added to the counter.</summary>
    Counter = 0,

    /// <summary>Point-in-time gauge; <see cref="MetricEvent.Value"/> is the absolute value.</summary>
    Gauge = 1,

    /// <summary>Distribution sample; <see cref="MetricEvent.Value"/> is one observation.</summary>
    Histogram = 2,
}

/// <summary>
/// Dimensional metrics interface for plugins. The pre-existing <see cref="Metrics"/> static counter is
/// intentionally untouched (legacy callers continue to compile); this interface provides the
/// name+value+tags shape that brainarr-style providers need for per-provider/per-model labels.
/// </summary>
/// <remarks>
/// <para>Source: brainarr Phase 4e adoption feedback ("common's <c>Metrics</c> is static no-op counters
/// only; brainarr needs name+tags shape for per-provider/per-model dimensional metrics").</para>
/// <para>Common ships <see cref="NullMetricsRecorder"/> as the default no-op implementation and
/// <see cref="ObservableMetricsRecorder"/> as a fanout adapter for plugins that want to forward events to
/// Prometheus, StatsD, OpenTelemetry, etc. Common itself does not take a dependency on any metrics
/// library.</para>
/// </remarks>
public interface IMetricsRecorder
{
    /// <summary>Records a counter increment.</summary>
    void Increment(string name, double value = 1.0, IReadOnlyDictionary<string, string>? tags = null);

    /// <summary>Records a gauge value.</summary>
    void Gauge(string name, double value, IReadOnlyDictionary<string, string>? tags = null);

    /// <summary>Records a histogram sample.</summary>
    void Histogram(string name, double value, IReadOnlyDictionary<string, string>? tags = null);
}

/// <summary>
/// No-op default <see cref="IMetricsRecorder"/>. Use <see cref="Instance"/> when no metrics backend is
/// configured. All methods are safe to call concurrently and never allocate.
/// </summary>
public sealed class NullMetricsRecorder : IMetricsRecorder
{
    /// <summary>The singleton no-op instance.</summary>
    public static readonly NullMetricsRecorder Instance = new();

    private NullMetricsRecorder() { }

    /// <inheritdoc/>
    public void Increment(string name, double value = 1.0, IReadOnlyDictionary<string, string>? tags = null) { }

    /// <inheritdoc/>
    public void Gauge(string name, double value, IReadOnlyDictionary<string, string>? tags = null) { }

    /// <inheritdoc/>
    public void Histogram(string name, double value, IReadOnlyDictionary<string, string>? tags = null) { }
}

/// <summary>
/// <see cref="IMetricsRecorder"/> that fans out every recorded sample to subscribed observers as a
/// <see cref="MetricEvent"/>. Plugins that want to integrate with Prometheus/StatsD/etc. subscribe and
/// translate events on their side, keeping common free of metrics-library dependencies.
/// </summary>
/// <remarks>
/// Subscribers are invoked synchronously inside the recording call. Exceptions raised by subscribers are
/// swallowed (best-effort fanout) — observability code must not break the path of the operation it
/// instruments.
/// </remarks>
public sealed class ObservableMetricsRecorder : IMetricsRecorder, IObservable<MetricEvent>
{
    private readonly ConcurrentDictionary<Guid, IObserver<MetricEvent>> _observers = new();

    /// <inheritdoc/>
    public IDisposable Subscribe(IObserver<MetricEvent> observer)
    {
        if (observer is null) throw new ArgumentNullException(nameof(observer));
        var id = Guid.NewGuid();
        _observers[id] = observer;
        return new Subscription(() => _observers.TryRemove(id, out _));
    }

    /// <inheritdoc/>
    public void Increment(string name, double value = 1.0, IReadOnlyDictionary<string, string>? tags = null)
        => Publish(new MetricEvent(name, value, MetricKind.Counter, tags, DateTimeOffset.UtcNow));

    /// <inheritdoc/>
    public void Gauge(string name, double value, IReadOnlyDictionary<string, string>? tags = null)
        => Publish(new MetricEvent(name, value, MetricKind.Gauge, tags, DateTimeOffset.UtcNow));

    /// <inheritdoc/>
    public void Histogram(string name, double value, IReadOnlyDictionary<string, string>? tags = null)
        => Publish(new MetricEvent(name, value, MetricKind.Histogram, tags, DateTimeOffset.UtcNow));

    private void Publish(MetricEvent ev)
    {
        if (_observers.IsEmpty) return;
        foreach (var kvp in _observers)
        {
            try { kvp.Value.OnNext(ev); }
            catch { /* swallow: observability must not break the calling path */ }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        public Subscription(Action dispose) { _dispose = dispose; }
        public void Dispose()
        {
            var d = System.Threading.Interlocked.Exchange(ref _dispose, null);
            d?.Invoke();
        }
    }
}
