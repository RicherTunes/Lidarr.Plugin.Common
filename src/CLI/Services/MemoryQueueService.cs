using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.CLI.Models;

namespace Lidarr.Plugin.Common.CLI.Services
{
    /// <summary>
    /// In-memory queue service for CLI download management
    /// Provides thread-safe queue operations with real-time updates
    /// </summary>
    public class MemoryQueueService : IQueueService
    {
        private readonly ConcurrentDictionary<string, CliDownloadItem> _queue;
        private bool _isPaused;
        private DateTime _lastActivity;

        public MemoryQueueService()
        {
            _queue = new ConcurrentDictionary<string, CliDownloadItem>();
            _isPaused = false;
            _lastActivity = DateTime.UtcNow;
        }

        public Task InitializeAsync()
        {
            // Memory-based service doesn't need initialization
            return Task.CompletedTask;
        }

        public Task<string> EnqueueAsync(CliDownloadItem item)
        {
            if (string.IsNullOrEmpty(item.Id))
            {
                item.Id = Guid.NewGuid().ToString();
            }

            item.Status = DownloadStatus.Pending;
            item.AddedDate = DateTime.UtcNow;
            item.ProgressPercent = 0;

            _queue.TryAdd(item.Id, item);
            _lastActivity = DateTime.UtcNow;

            return Task.FromResult(item.Id);
        }

        public Task<List<CliDownloadItem>> GetQueueAsync()
        {
            var items = _queue.Values
                .OrderBy(x => x.AddedDate)
                .ToList();

            return Task.FromResult(items);
        }

        public Task<CliDownloadItem> GetItemAsync(string itemId)
        {
            if (_queue.TryGetValue(itemId, out var item))
            {
                return Task.FromResult(item);
            }
            throw new KeyNotFoundException($"Queue item '{itemId}' not found.");
        }

        public Task UpdateItemAsync(string itemId, DownloadStatus status, int progressPercent = 0, string statusMessage = null)
        {
            if (_queue.TryGetValue(itemId, out var item))
            {
                item.Status = status;
                item.ProgressPercent = Math.Max(0, Math.Min(100, progressPercent));
                
                if (!string.IsNullOrEmpty(statusMessage))
                {
                    item.StatusMessage = statusMessage;
                }

                // Update completion time for finished items
                if (status == DownloadStatus.Completed || status == DownloadStatus.Failed)
                {
                    item.CompletedDate = DateTime.UtcNow;
                }
                else if (status == DownloadStatus.Downloading)
                {
                    item.StartedDate = item.StartedDate ?? DateTime.UtcNow;
                }

                _lastActivity = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        }

        public Task RemoveItemAsync(string itemId)
        {
            _queue.TryRemove(itemId, out _);
            _lastActivity = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        public Task ClearCompletedAsync()
        {
            var completedItems = _queue.Values
                .Where(x => x.Status == DownloadStatus.Completed || x.Status == DownloadStatus.Failed)
                .ToList();

            foreach (var item in completedItems)
            {
                _queue.TryRemove(item.Id, out _);
            }

            if (completedItems.Any())
            {
                _lastActivity = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        }

        public Task<QueueStatistics> GetStatisticsAsync()
        {
            var items = _queue.Values.ToList();

            var stats = new QueueStatistics
            {
                TotalItems = items.Count,
                CompletedItems = items.Count(x => x.Status == DownloadStatus.Completed),
                FailedItems = items.Count(x => x.Status == DownloadStatus.Failed),
                InProgressItems = items.Count(x => x.Status == DownloadStatus.Downloading),
                PendingItems = items.Count(x => x.Status == DownloadStatus.Pending),
                LastActivity = _lastActivity,
                TotalBytesDownloaded = items
                    .Where(x => x.Status == DownloadStatus.Completed)
                    .Sum(x => x.TotalBytes)
            };

            return Task.FromResult(stats);
        }

        public Task SetQueueStateAsync(bool isPaused)
        {
            _isPaused = isPaused;
            _lastActivity = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        public Task<bool> IsQueuePausedAsync()
        {
            return Task.FromResult(_isPaused);
        }
    }
}
