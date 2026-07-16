using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Lidarr.Plugin.Common.Services.Performance;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Behavior-pinning tests for <see cref="PerformanceMonitor"/>. The flush timer is kept
    /// out of play with a very long interval — the only timer-driven behavior asserted is the
    /// synchronous final flush on Dispose, so there are no wall-clock races.
    /// </summary>
    [Trait("Category", "Unit")]
    public class PerformanceMonitorTests
    {
        /// <summary>An interval long enough that the periodic timer never fires during a test.</summary>
        private static readonly TimeSpan Never = TimeSpan.FromHours(1);

        private sealed class FlushCapturingMonitor : PerformanceMonitor
        {
            public int FlushCount;
            public PerformanceSummary? LastFlushed;

            public FlushCapturingMonitor(TimeSpan interval)
                : base(interval)
            {
            }

            protected override void OnMetricsFlush(PerformanceSummary summary)
            {
                Interlocked.Increment(ref FlushCount);
                LastFlushed = summary;
            }
        }

        [Fact]
        public void RecordApiCall_AggregatesCallsErrorsAndCacheHits()
        {
            using var monitor = new PerformanceMonitor(Never);

            monitor.RecordApiCall("albums", TimeSpan.FromMilliseconds(100), fromCache: false, statusCode: 200);
            monitor.RecordApiCall("albums", TimeSpan.FromMilliseconds(300), fromCache: true, statusCode: 200);
            monitor.RecordApiCall("albums", TimeSpan.FromMilliseconds(200), fromCache: false, statusCode: 500);

            var summary = monitor.GetSummary();
            var op = summary.Operations["albums"];

            Assert.Equal(MetricType.ApiCall, op.Type);
            Assert.Equal(3, op.TotalCalls);
            Assert.Equal(TimeSpan.FromMilliseconds(600), op.TotalDuration);
            Assert.Equal(200, op.AverageDuration); // 600ms / 3 calls
            Assert.Equal(1, op.ErrorCount);        // only the 500
            Assert.Equal(100.0 / 3, op.ErrorRate, precision: 5);
            Assert.Equal(100.0 / 3, op.CacheHitRate, precision: 5);
            Assert.Equal(3, summary.TotalOperations);
            Assert.Equal(1, summary.TotalErrors);
        }

        [Fact]
        public void RecordApiCall_NullStatusCode_IsNotCountedAsError()
        {
            using var monitor = new PerformanceMonitor(Never);

            monitor.RecordApiCall("search", TimeSpan.FromMilliseconds(50), fromCache: true, statusCode: null);

            var op = monitor.GetSummary().Operations["search"];
            Assert.Equal(0, op.ErrorCount);
            Assert.Equal(1, op.TotalCalls);
        }

        [Fact]
        public void RecordCacheOperation_TracksHitRate()
        {
            using var monitor = new PerformanceMonitor(Never);

            monitor.RecordCacheOperation("response", "key1", hit: true);
            monitor.RecordCacheOperation("response", "key2", hit: false);
            monitor.RecordCacheOperation("response", "key3", hit: true, duration: TimeSpan.FromMilliseconds(5));

            var op = monitor.GetSummary().Operations["response"];
            Assert.Equal(MetricType.Cache, op.Type);
            Assert.Equal(3, op.TotalCalls);
            Assert.Equal(200.0 / 3, op.CacheHitRate, precision: 5); // 2 hits of 3
            Assert.Equal(TimeSpan.FromMilliseconds(5), op.TotalDuration); // only explicit durations counted
        }

        [Fact]
        public void RecordDownload_AccumulatesBytesAndFailures()
        {
            using var monitor = new PerformanceMonitor(Never);

            monitor.RecordDownload("t1", TimeSpan.FromSeconds(1), fileSize: 1000, success: true);
            monitor.RecordDownload("t2", TimeSpan.FromSeconds(2), fileSize: 2500, success: false, errorMessage: "boom");

            var op = monitor.GetSummary().Operations["Track Downloads"];
            Assert.Equal(MetricType.Download, op.Type);
            Assert.Equal(2, op.TotalCalls);
            Assert.Equal(3500, op.TotalBytes);
            Assert.Equal(1, op.ErrorCount);
        }

        [Fact]
        public void RecordOperation_CustomMetrics_AndOverallErrorRate()
        {
            using var monitor = new PerformanceMonitor(Never);

            monitor.RecordOperation("tagging", TimeSpan.FromMilliseconds(10));
            monitor.RecordOperation("tagging", TimeSpan.FromMilliseconds(10), success: false);

            var summary = monitor.GetSummary();
            Assert.Equal(MetricType.Custom, summary.Operations["tagging"].Type);
            Assert.Equal(2, summary.TotalOperations);
            Assert.Equal(1, summary.TotalErrors);
            Assert.Equal(50, summary.OverallErrorRate);
        }

        [Fact]
        public void OperationsWithSameNameDifferentKind_DoNotCollide()
        {
            using var monitor = new PerformanceMonitor(Never);

            // "api_x" and "custom_x" are distinct metric buckets even though both are named x;
            // the summary keys by display name so the LAST one enumerated wins the dictionary
            // slot, but the per-bucket counts must not bleed into each other.
            monitor.RecordApiCall("x", TimeSpan.FromMilliseconds(1), statusCode: 200);
            monitor.RecordOperation("x", TimeSpan.FromMilliseconds(1));

            var summary = monitor.GetSummary();
            Assert.Equal(2, summary.TotalOperations);
            Assert.Equal(1, summary.Operations["x"].TotalCalls);
        }

        [Fact]
        public void GetRecentEvents_ReturnsRecordedEvents_CappedAtRequestedCount()
        {
            using var monitor = new PerformanceMonitor(Never);

            for (var i = 0; i < 5; i++)
            {
                monitor.RecordOperation($"op{i}", TimeSpan.FromMilliseconds(1));
            }

            var recent = monitor.GetRecentEvents(count: 3).ToList();
            Assert.Equal(3, recent.Count);
            // TakeLast semantics: the newest events win.
            Assert.Equal(new[] { "op2", "op3", "op4" }, recent.Select(e => e.Name));
        }

        [Fact]
        public void EventBuffer_IsBoundedAtOneThousand()
        {
            using var monitor = new PerformanceMonitor(Never);

            for (var i = 0; i < 1100; i++)
            {
                monitor.RecordOperation("op", TimeSpan.Zero);
            }

            Assert.True(monitor.GetRecentEvents(int.MaxValue).Count() <= 1000);
        }

        [Fact]
        public void Reset_ClearsMetricsAndEvents()
        {
            using var monitor = new PerformanceMonitor(Never);
            monitor.RecordApiCall("albums", TimeSpan.FromMilliseconds(1), statusCode: 200);

            monitor.Reset();

            var summary = monitor.GetSummary();
            Assert.Empty(summary.Operations);
            Assert.Equal(0, summary.TotalOperations);
            Assert.Empty(monitor.GetRecentEvents());
        }

        [Fact]
        public void Dispose_PerformsExactlyOneFinalFlush_WithCurrentSummary()
        {
            var monitor = new FlushCapturingMonitor(Never);
            monitor.RecordOperation("op", TimeSpan.FromMilliseconds(1));

            monitor.Dispose();
            monitor.Dispose(); // idempotent: no second flush

            Assert.Equal(1, monitor.FlushCount);
            Assert.NotNull(monitor.LastFlushed);
            Assert.Equal(1, monitor.LastFlushed!.TotalOperations);
        }

        [Fact]
        public void GetSummary_EmptyMonitor_IsSane()
        {
            using var monitor = new PerformanceMonitor(Never);

            var summary = monitor.GetSummary();

            Assert.Empty(summary.Operations);
            Assert.Equal(0, summary.TotalOperations);
            Assert.Equal(0, summary.OverallErrorRate);
        }
    }
}
