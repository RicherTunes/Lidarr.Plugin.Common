using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Contract for host-managed download orchestration.
    /// </summary>
    public interface IDownloadClient : IAsyncDisposable
    {
        /// <summary>
        /// Performs any network or authentication initialization required before handling requests.
        /// </summary>
        ValueTask<PluginValidationResult> InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Queues a download for the specified album and returns a host-visible identifier.
        /// </summary>
        ValueTask<string> EnqueueAlbumDownloadAsync(string albumId, string outputPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Attempts to remove an existing download from the queue.
        /// </summary>
        ValueTask<bool> RemoveDownloadAsync(string downloadId, bool deleteData = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns a snapshot of all active downloads.
        /// </summary>
        ValueTask<IReadOnlyList<StreamingDownloadItem>> GetActiveDownloadsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the state for a single download, or <c>null</c> if none exists.
        /// </summary>
        ValueTask<StreamingDownloadItem?> GetDownloadAsync(string downloadId, CancellationToken cancellationToken = default);
    }
}
