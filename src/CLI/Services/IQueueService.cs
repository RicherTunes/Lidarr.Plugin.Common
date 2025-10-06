using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.CLI.Models;

namespace Lidarr.Plugin.Common.CLI.Services
{
    /// <summary>
    /// Interface for managing download queue in CLI applications
    /// Handles queuing, progress tracking, and status management for downloads
    /// </summary>
    public interface IQueueService
    {
        /// <summary>
        /// Initialize the queue service
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// Add item to download queue
        /// </summary>
        Task<string> EnqueueAsync(CliDownloadItem item);

        /// <summary>
        /// Get all items in queue
        /// </summary>
        Task<List<CliDownloadItem>> GetQueueAsync();

        /// <summary>
        /// Get specific queue item by ID
        /// </summary>
        Task<CliDownloadItem> GetItemAsync(string itemId);

        /// <summary>
        /// Try to get specific queue item by ID; returns null when not found.
        /// </summary>
        Task<CliDownloadItem?> TryGetItemAsync(string itemId);

        /// <summary>
        /// Update queue item status and progress
        /// </summary>
        Task UpdateItemAsync(string itemId, DownloadStatus status, int progressPercent = 0, string statusMessage = null);

        /// <summary>
        /// Remove item from queue
        /// </summary>
        Task RemoveItemAsync(string itemId);

        /// <summary>
        /// Clear completed items from queue
        /// </summary>
        Task ClearCompletedAsync();

        /// <summary>
        /// Get queue statistics
        /// </summary>
        Task<QueueStatistics> GetStatisticsAsync();

        /// <summary>
        /// Pause/resume queue processing
        /// </summary>
        Task SetQueueStateAsync(bool isPaused);

        /// <summary>
        /// Get current queue state
        /// </summary>
        Task<bool> IsQueuePausedAsync();
    }

    /// <summary>
    /// Queue statistics for reporting and monitoring
    /// </summary>
    public class QueueStatistics
    {
        public int TotalItems { get; set; }
        public int CompletedItems { get; set; }
        public int FailedItems { get; set; }
        public int InProgressItems { get; set; }
        public int PendingItems { get; set; }
        public DateTime LastActivity { get; set; }
        public long TotalBytesDownloaded { get; set; }
    }
}
