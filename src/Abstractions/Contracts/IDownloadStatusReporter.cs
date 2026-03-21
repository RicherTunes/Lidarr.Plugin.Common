using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Reports download progress and status to the host.
    /// Bridge plugins use this to communicate download state for UI display.
    /// </summary>
    public interface IDownloadStatusReporter
    {
        /// <summary>
        /// Gets the current download status.
        /// </summary>
        DownloadStatus Status { get; }

        /// <summary>
        /// Reports download progress.
        /// </summary>
        /// <param name="progress">Progress information</param>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask ReportProgressAsync(AlbumDownloadProgress progress, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reports download completion.
        /// </summary>
        /// <param name="albumId">Completed album ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask ReportCompletedAsync(string albumId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reports download failure.
        /// </summary>
        /// <param name="albumId">Failed album ID</param>
        /// <param name="error">Error details</param>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask ReportFailedAsync(string albumId, Exception error, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents download operational status.
    /// </summary>
    public enum DownloadStatus
    {
        /// <summary>
        /// No download in progress.
        /// </summary>
        Idle,

        /// <summary>
        /// Download is queued.
        /// </summary>
        Queued,

        /// <summary>
        /// Download is in progress.
        /// </summary>
        Downloading,

        /// <summary>
        /// Download is paused.
        /// </summary>
        Paused,

        /// <summary>
        /// Download completed successfully.
        /// </summary>
        Completed,

        /// <summary>
        /// Download failed.
        /// </summary>
        Failed
    }
}
