using System;
using System.Net;
using System.Threading;

namespace Lidarr.Plugin.Common.Utilities
{
    internal sealed class DownloadTelemetryCounters
    {
        private long _retryCount;
        private long _tooManyRequestsCount;

        public int RetryCount => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _retryCount));
        public int TooManyRequestsCount => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _tooManyRequestsCount));

        public void RecordRetry(HttpStatusCode statusCode)
        {
            Interlocked.Increment(ref _retryCount);
            if (statusCode == HttpStatusCode.TooManyRequests)
            {
                Interlocked.Increment(ref _tooManyRequestsCount);
            }
        }
    }

    internal static class DownloadTelemetryContext
    {
        private static readonly AsyncLocal<DownloadTelemetryCounters?> Current = new();

        public static DownloadTelemetryCounters? Get() => Current.Value;

        public static IDisposable Begin(DownloadTelemetryCounters counters)
        {
            if (counters == null) throw new ArgumentNullException(nameof(counters));

            var prior = Current.Value;
            Current.Value = counters;
            return new Scope(prior);
        }

        public static void RecordRetry(HttpStatusCode statusCode)
        {
            Current.Value?.RecordRetry(statusCode);
        }

        private sealed class Scope : IDisposable
        {
            private readonly DownloadTelemetryCounters? _prior;
            private int _disposed;

            public Scope(DownloadTelemetryCounters? prior)
            {
                _prior = prior;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                Current.Value = _prior;
            }
        }
    }
}

