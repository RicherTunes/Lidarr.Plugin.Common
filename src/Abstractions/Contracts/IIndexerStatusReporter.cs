using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Reports indexer status changes to the host.
    /// Bridge plugins use this to communicate status (idle, searching, error) for UI display.
    /// </summary>
    public interface IIndexerStatusReporter
    {
        /// <summary>
        /// Gets the current indexer status.
        /// </summary>
        IndexerStatus CurrentStatus { get; }

        /// <summary>
        /// Reports a status change to the host.
        /// </summary>
        /// <param name="status">New status</param>
        /// <param name="message">Optional status message</param>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask ReportStatusAsync(IndexerStatus status, string? message = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reports an error condition.
        /// </summary>
        /// <param name="error">Error details</param>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask ReportErrorAsync(Exception error, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents indexer operational status.
    /// </summary>
    public enum IndexerStatus
    {
        /// <summary>
        /// Indexer is idle and ready for requests.
        /// </summary>
        Idle,

        /// <summary>
        /// Indexer is performing a search operation.
        /// </summary>
        Searching,

        /// <summary>
        /// Indexer is authenticating.
        /// </summary>
        Authenticating,

        /// <summary>
        /// Indexer is rate-limited and waiting.
        /// </summary>
        RateLimited,

        /// <summary>
        /// Indexer encountered an error.
        /// </summary>
        Error
    }
}
