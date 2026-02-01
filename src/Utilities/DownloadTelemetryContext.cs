using System;
using System.ComponentModel;
using System.Net;
using System.Threading;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Internal counter storage for download telemetry.
    /// Plugins should use DownloadTelemetryContext.RecordRetry() to record retries,
    /// not manipulate counters directly.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public sealed class DownloadTelemetryCounters
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

    /// <summary>
    /// AsyncLocal context for download telemetry during a download operation.
    /// Automatically propagated through async/await boundaries.
    /// </summary>
    /// <remarks>
    /// This is an advanced API for plugins that implement custom retry logic outside
    /// of Common's resilience pipelines. Most plugins should use IStreamingStreamProvider
    /// or HttpClientExtensions.ExecuteWithResilienceAsync which handle telemetry automatically.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static class DownloadTelemetryContext
    {
        private static readonly AsyncLocal<DownloadTelemetryCounters?> Current = new();

        /// <summary>
        /// Gets the current telemetry context for this async operation.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public static DownloadTelemetryCounters? Get() => Current.Value;

        /// <summary>
        /// Begins a telemetry scope for a download operation.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public static IDisposable Begin(DownloadTelemetryCounters counters)
        {
            if (counters == null) throw new ArgumentNullException(nameof(counters));

            var prior = Current.Value;
            Current.Value = counters;
            return new Scope(prior);
        }

        /// <summary>
        /// Records a retry attempt for telemetry. Call this in custom retry loops
        /// when using raw HttpClient instead of Common's resilience helpers.
        /// </summary>
        /// <param name="statusCode">The HTTP status code that triggered the retry.</param>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
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

