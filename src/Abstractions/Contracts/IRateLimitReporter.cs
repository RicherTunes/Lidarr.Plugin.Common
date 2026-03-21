using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Reports rate limit events to the host for telemetry and UI display.
    /// Bridge plugins use this to communicate when rate limits are hit.
    /// </summary>
    public interface IRateLimitReporter
    {
        /// <summary>
        /// Gets the current rate limit status.
        /// </summary>
        RateLimitStatus Status { get; }

        /// <summary>
        /// Reports a rate limit event.
        /// </summary>
        /// <param name="retryAfter">When the rate limit will reset</param>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask ReportRateLimitAsync(TimeSpan retryAfter, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reports that rate limiting has cleared.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask ReportRateLimitClearedAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Reports a backoff delay is being applied.
        /// </summary>
        /// <param name="delay">Backoff duration</param>
        /// <param name="reason">Reason for backoff</param>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask ReportBackoffAsync(TimeSpan delay, string reason, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents rate limit status.
    /// </summary>
    public class RateLimitStatus
    {
        /// <summary>
        /// Whether currently rate-limited.
        /// </summary>
        public bool IsRateLimited { get; init; }

        /// <summary>
        /// When the rate limit will reset (if applicable).
        /// </summary>
        public DateTimeOffset? ResetAt { get; init; }

        /// <summary>
        /// Remaining requests in current window (if known).
        /// </summary>
        public int? RemainingRequests { get; init; }

        /// <summary>
        /// Total requests allowed in window (if known).
        /// </summary>
        public int? TotalRequests { get; init; }
    }
}
