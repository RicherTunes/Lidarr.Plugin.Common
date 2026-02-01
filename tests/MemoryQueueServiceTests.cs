using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.CLI.Models;
using Lidarr.Plugin.Common.CLI.Services;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class MemoryQueueServiceTests
    {
        [Fact]
        public async Task InitializeAsync_CompletesSuccessfully()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act
            var action = async () => await service.InitializeAsync();

            // Assert
            await action();
        }

        #region Enqueue Operations

        [Fact]
        public async Task EnqueueAsync_SingleItem_AddsItemToQueue()
        {
            // Arrange
            var service = new MemoryQueueService();
            var item = CreateTestItem(title: "Test Song");

            // Act
            var itemId = await service.EnqueueAsync(item);

            // Assert
            Assert.NotEmpty(itemId);
            var queue = await service.GetQueueAsync();
            Assert.Single(queue);
            Assert.Equal(itemId, queue[0].Id);
            Assert.Equal(DownloadStatus.Pending, queue[0].Status);
        }

        [Fact]
        public async Task EnqueueAsync_WithExistingId_PreservesId()
        {
            // Arrange
            var service = new MemoryQueueService();
            var expectedId = "my-custom-id";
            var item = CreateTestItem();
            item.Id = expectedId;

            // Act
            var itemId = await service.EnqueueAsync(item);

            // Assert
            Assert.Equal(expectedId, itemId);
        }

        [Fact]
        public async Task EnqueueAsync_WithEmptyId_GeneratesNewId()
        {
            // Arrange
            var service = new MemoryQueueService();
            var item = CreateTestItem();
            item.Id = string.Empty;

            // Act
            var itemId = await service.EnqueueAsync(item);

            // Assert
            Assert.NotEmpty(itemId);
            Assert.NotEqual(string.Empty, itemId);
        }

        [Fact]
        public async Task EnqueueAsync_SetsInitialProperties()
        {
            // Arrange
            var service = new MemoryQueueService();
            var item = CreateTestItem();
            item.Status = DownloadStatus.Completed; // Should be overridden
            item.ProgressPercent = 50; // Should be overridden

            // Act
            var itemId = await service.EnqueueAsync(item);
            var queue = await service.GetQueueAsync();

            // Assert
            var queuedItem = queue[0];
            Assert.Equal(DownloadStatus.Pending, queuedItem.Status);
            Assert.Equal(0, queuedItem.ProgressPercent);
            Assert.NotNull(queuedItem.AddedDate);
        }

        [Fact]
        public async Task EnqueueAsync_MultipleItems_AddsAllItems()
        {
            // Arrange
            var service = new MemoryQueueService();
            var items = new[]
            {
                CreateTestItem(title: "Song 1"),
                CreateTestItem(title: "Song 2"),
                CreateTestItem(title: "Song 3")
            };

            // Act
            var itemIds = new List<string>();
            foreach (var item in items)
            {
                itemIds.Add(await service.EnqueueAsync(item));
            }

            // Assert
            var queue = await service.GetQueueAsync();
            Assert.Equal(3, queue.Count);
            Assert.All(itemIds, id => Assert.NotEmpty(id));
        }

        [Fact]
        public async Task EnqueueAsync_MultipleItems_ItemsOrderedByAddedDate()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act
            await service.EnqueueAsync(CreateTestItem(title: "First"));
            await Task.Delay(10); // Ensure time difference
            await service.EnqueueAsync(CreateTestItem(title: "Second"));
            await Task.Delay(10);
            await service.EnqueueAsync(CreateTestItem(title: "Third"));

            // Assert
            var queue = await service.GetQueueAsync();
            Assert.Equal("First", queue[0].Title);
            Assert.Equal("Second", queue[1].Title);
            Assert.Equal("Third", queue[2].Title);
        }

        #endregion

        #region GetQueue Operations

        [Fact]
        public async Task GetQueueAsync_EmptyQueue_ReturnsEmptyList()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act
            var queue = await service.GetQueueAsync();

            // Assert
            Assert.NotNull(queue);
            Assert.Empty(queue);
        }

        [Fact]
        public async Task GetQueueAsync_WithItems_ReturnsItemsOrderedByAddedDate()
        {
            // Arrange
            var service = new MemoryQueueService();
            await service.EnqueueAsync(CreateTestItem(title: "A"));
            await Task.Delay(10);
            await service.EnqueueAsync(CreateTestItem(title: "B"));
            await Task.Delay(10);
            await service.EnqueueAsync(CreateTestItem(title: "C"));

            // Act
            var queue = await service.GetQueueAsync();

            // Assert
            Assert.Equal(3, queue.Count);
            Assert.Equal("A", queue[0].Title);
            Assert.Equal("B", queue[1].Title);
            Assert.Equal("C", queue[2].Title);
        }

        #endregion

        #region GetItem Operations

        [Fact]
        public async Task GetItemAsync_ExistingItem_ReturnsItem()
        {
            // Arrange
            var service = new MemoryQueueService();
            var item = CreateTestItem(title: "Found Item");
            var itemId = await service.EnqueueAsync(item);

            // Act
            var retrievedItem = await service.GetItemAsync(itemId);

            // Assert
            Assert.NotNull(retrievedItem);
            Assert.Equal(itemId, retrievedItem.Id);
            Assert.Equal("Found Item", retrievedItem.Title);
        }

        [Fact]
        public async Task GetItemAsync_NonExistentItem_ThrowsKeyNotFoundException()
        {
            // Arrange
            var service = new MemoryQueueService();
            var nonExistentId = "does-not-exist";

            // Act & Assert
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                service.GetItemAsync(nonExistentId));
        }

        [Fact]
        public async Task GetItemAsync_NonExistentItem_ExceptionContainsItemId()
        {
            // Arrange
            var service = new MemoryQueueService();
            var nonExistentId = "missing-id-12345";

            // Act
            var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                service.GetItemAsync(nonExistentId));

            // Assert
            Assert.Contains(nonExistentId, ex.Message);
        }

        [Fact]
        public async Task TryGetItemAsync_ExistingItem_ReturnsItem()
        {
            // Arrange
            var service = new MemoryQueueService();
            var item = CreateTestItem(title: "Found Item");
            var itemId = await service.EnqueueAsync(item);

            // Act
            var retrievedItem = await service.TryGetItemAsync(itemId);

            // Assert
            Assert.NotNull(retrievedItem);
            Assert.Equal(itemId, retrievedItem.Id);
            Assert.Equal("Found Item", retrievedItem.Title);
        }

        [Fact]
        public async Task TryGetItemAsync_NonExistentItem_ReturnsNull()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act
            var retrievedItem = await service.TryGetItemAsync("does-not-exist");

            // Assert
            Assert.Null(retrievedItem);
        }

        #endregion

        #region UpdateItem Operations

        [Fact]
        public async Task UpdateItemAsync_ExistingItem_UpdatesStatus()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());

            // Act
            await service.UpdateItemAsync(itemId, DownloadStatus.Downloading);
            var item = await service.GetItemAsync(itemId);

            // Assert
            Assert.Equal(DownloadStatus.Downloading, item.Status);
        }

        [Fact]
        public async Task UpdateItemAsync_ExistingItem_UpdatesProgressPercent()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());

            // Act
            await service.UpdateItemAsync(itemId, DownloadStatus.Downloading, progressPercent: 65);
            var item = await service.GetItemAsync(itemId);

            // Assert
            Assert.Equal(65, item.ProgressPercent);
        }

        [Fact]
        public async Task UpdateItemAsync_ProgressBelowZero_ClampsToZero()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());

            // Act
            await service.UpdateItemAsync(itemId, DownloadStatus.Downloading, progressPercent: -10);
            var item = await service.GetItemAsync(itemId);

            // Assert
            Assert.Equal(0, item.ProgressPercent);
        }

        [Fact]
        public async Task UpdateItemAsync_ProgressAbove100_ClampsTo100()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());

            // Act
            await service.UpdateItemAsync(itemId, DownloadStatus.Downloading, progressPercent: 150);
            var item = await service.GetItemAsync(itemId);

            // Assert
            Assert.Equal(100, item.ProgressPercent);
        }

        [Fact]
        public async Task UpdateItemAsync_WithStatusMessage_UpdatesMessage()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());

            // Act
            await service.UpdateItemAsync(itemId, DownloadStatus.Downloading, 0, "Downloading track 3 of 10");
            var item = await service.GetItemAsync(itemId);

            // Assert
            Assert.Equal("Downloading track 3 of 10", item.StatusMessage);
        }

        [Fact]
        public async Task UpdateItemAsync_NullStatusMessage_DoesNotOverwrite()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());
            await service.UpdateItemAsync(itemId, DownloadStatus.Downloading, 0, "Original message");

            // Act
            await service.UpdateItemAsync(itemId, DownloadStatus.Completed, 100, null!);
            var item = await service.GetItemAsync(itemId);

            // Assert
            Assert.Equal("Original message", item.StatusMessage);
        }

        [Fact]
        public async Task UpdateItemAsync_CompletedStatus_SetsCompletedDate()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());

            // Act
            await service.UpdateItemAsync(itemId, DownloadStatus.Completed);
            var item = await service.GetItemAsync(itemId);

            // Assert
            Assert.NotNull(item.CompletedDate);
        }

        [Fact]
        public async Task UpdateItemAsync_FailedStatus_SetsCompletedDate()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());

            // Act
            await service.UpdateItemAsync(itemId, DownloadStatus.Failed);
            var item = await service.GetItemAsync(itemId);

            // Assert
            Assert.NotNull(item.CompletedDate);
        }

        [Fact]
        public async Task UpdateItemAsync_DownloadingStatus_SetsStartedDate()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());

            // Act
            await service.UpdateItemAsync(itemId, DownloadStatus.Downloading);
            var item = await service.GetItemAsync(itemId);

            // Assert
            Assert.NotNull(item.StartedDate);
        }

        [Fact]
        public async Task UpdateItemAsync_DownloadingStatus_PreservesExistingStartedDate()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());
            await service.UpdateItemAsync(itemId, DownloadStatus.Downloading);
            var originalStartedDate = (await service.GetItemAsync(itemId)).StartedDate;

            // Act
            await service.UpdateItemAsync(itemId, DownloadStatus.Downloading, 50);
            var item = await service.GetItemAsync(itemId);

            // Assert
            Assert.Equal(originalStartedDate, item.StartedDate);
        }

        [Fact]
        public async Task UpdateItemAsync_NonExistentItem_DoesNotThrow()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act & Assert - Should complete without throwing
            await service.UpdateItemAsync("does-not-exist", DownloadStatus.Completed);
        }

        #endregion

        #region RemoveItem Operations

        [Fact]
        public async Task RemoveItemAsync_ExistingItem_RemovesItem()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());

            // Act
            await service.RemoveItemAsync(itemId);
            var queue = await service.GetQueueAsync();

            // Assert
            Assert.Empty(queue);
        }

        [Fact]
        public async Task RemoveItemAsync_NonExistentItem_DoesNotThrow()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act & Assert - Should complete without throwing
            await service.RemoveItemAsync("does-not-exist");
        }

        [Fact]
        public async Task RemoveItemAsync_OneOfManyItems_RemovesCorrectItem()
        {
            // Arrange
            var service = new MemoryQueueService();
            var id1 = await service.EnqueueAsync(CreateTestItem(title: "Item 1"));
            var id2 = await service.EnqueueAsync(CreateTestItem(title: "Item 2"));
            var id3 = await service.EnqueueAsync(CreateTestItem(title: "Item 3"));

            // Act
            await service.RemoveItemAsync(id2);
            var queue = await service.GetQueueAsync();

            // Assert
            Assert.Equal(2, queue.Count);
            Assert.DoesNotContain(queue, x => x.Id == id2);
            Assert.Contains(queue, x => x.Id == id1);
            Assert.Contains(queue, x => x.Id == id3);
        }

        #endregion

        #region ClearCompleted Operations

        [Fact]
        public async Task ClearCompletedAsync_WithCompletedItems_RemovesCompletedItems()
        {
            // Arrange
            var service = new MemoryQueueService();
            var completedId = await service.EnqueueAsync(CreateTestItem(title: "Completed"));
            var pendingId = await service.EnqueueAsync(CreateTestItem(title: "Pending"));
            var failedId = await service.EnqueueAsync(CreateTestItem(title: "Failed"));

            await service.UpdateItemAsync(completedId, DownloadStatus.Completed);
            await service.UpdateItemAsync(failedId, DownloadStatus.Failed);

            // Act
            await service.ClearCompletedAsync();
            var queue = await service.GetQueueAsync();

            // Assert
            Assert.Single(queue);
            Assert.Equal(pendingId, queue[0].Id);
        }

        [Fact]
        public async Task ClearCompletedAsync_AllPending_DoesNotRemoveAny()
        {
            // Arrange
            var service = new MemoryQueueService();
            await service.EnqueueAsync(CreateTestItem(title: "Item 1"));
            await service.EnqueueAsync(CreateTestItem(title: "Item 2"));
            await service.EnqueueAsync(CreateTestItem(title: "Item 3"));

            // Act
            await service.ClearCompletedAsync();
            var queue = await service.GetQueueAsync();

            // Assert
            Assert.Equal(3, queue.Count);
        }

        [Fact]
        public async Task ClearCompletedAsync_AllCompleted_RemovesAll()
        {
            // Arrange
            var service = new MemoryQueueService();
            var id1 = await service.EnqueueAsync(CreateTestItem(title: "Item 1"));
            var id2 = await service.EnqueueAsync(CreateTestItem(title: "Item 2"));

            await service.UpdateItemAsync(id1, DownloadStatus.Completed);
            await service.UpdateItemAsync(id2, DownloadStatus.Completed);

            // Act
            await service.ClearCompletedAsync();
            var queue = await service.GetQueueAsync();

            // Assert
            Assert.Empty(queue);
        }

        [Fact]
        public async Task ClearCompletedAsync_AllFailed_RemovesAll()
        {
            // Arrange
            var service = new MemoryQueueService();
            var id1 = await service.EnqueueAsync(CreateTestItem(title: "Item 1"));
            var id2 = await service.EnqueueAsync(CreateTestItem(title: "Item 2"));

            await service.UpdateItemAsync(id1, DownloadStatus.Failed);
            await service.UpdateItemAsync(id2, DownloadStatus.Failed);

            // Act
            await service.ClearCompletedAsync();
            var queue = await service.GetQueueAsync();

            // Assert
            Assert.Empty(queue);
        }

        [Fact]
        public async Task ClearCompletedAsync_WithDownloadingItems_KeepsDownloading()
        {
            // Arrange
            var service = new MemoryQueueService();
            var completedId = await service.EnqueueAsync(CreateTestItem(title: "Completed"));
            var downloadingId = await service.EnqueueAsync(CreateTestItem(title: "Downloading"));

            await service.UpdateItemAsync(completedId, DownloadStatus.Completed);
            await service.UpdateItemAsync(downloadingId, DownloadStatus.Downloading);

            // Act
            await service.ClearCompletedAsync();
            var queue = await service.GetQueueAsync();

            // Assert
            Assert.Single(queue);
            Assert.Equal(downloadingId, queue[0].Id);
        }

        [Fact]
        public async Task ClearCompletedAsync_EmptyQueue_DoesNotThrow()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act & Assert - Should complete without throwing
            await service.ClearCompletedAsync();
        }

        #endregion

        #region Statistics Operations

        [Fact]
        public async Task GetStatisticsAsync_EmptyQueue_ReturnsZeroCounts()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act
            var stats = await service.GetStatisticsAsync();

            // Assert
            Assert.Equal(0, stats.TotalItems);
            Assert.Equal(0, stats.CompletedItems);
            Assert.Equal(0, stats.FailedItems);
            Assert.Equal(0, stats.InProgressItems);
            Assert.Equal(0, stats.PendingItems);
        }

        [Fact]
        public async Task GetStatisticsAsync_MixedStatuses_CorrectCounts()
        {
            // Arrange
            var service = new MemoryQueueService();
            var pendingId = await service.EnqueueAsync(CreateTestItem(title: "Pending"));
            var downloadingId = await service.EnqueueAsync(CreateTestItem(title: "Downloading"));
            var completedId = await service.EnqueueAsync(CreateTestItem(title: "Completed"));
            var failedId = await service.EnqueueAsync(CreateTestItem(title: "Failed"));

            await service.UpdateItemAsync(pendingId, DownloadStatus.Pending);
            await service.UpdateItemAsync(downloadingId, DownloadStatus.Downloading);
            await service.UpdateItemAsync(completedId, DownloadStatus.Completed);
            await service.UpdateItemAsync(failedId, DownloadStatus.Failed);

            // Act
            var stats = await service.GetStatisticsAsync();

            // Assert
            Assert.Equal(4, stats.TotalItems);
            Assert.Equal(1, stats.CompletedItems);
            Assert.Equal(1, stats.FailedItems);
            Assert.Equal(1, stats.InProgressItems);
            Assert.Equal(1, stats.PendingItems);
        }

        [Fact]
        public async Task GetStatisticsAsync_WithBytes_SumsCompletedBytes()
        {
            // Arrange
            var service = new MemoryQueueService();
            var id1 = await service.EnqueueAsync(CreateTestItem(title: "Item 1"));
            var id2 = await service.EnqueueAsync(CreateTestItem(title: "Item 2"));

            var item1 = await service.GetItemAsync(id1);
            item1.TotalBytes = 1_000_000;
            await service.UpdateItemAsync(id1, DownloadStatus.Completed);

            var item2 = await service.GetItemAsync(id2);
            item2.TotalBytes = 2_500_000;
            await service.UpdateItemAsync(id2, DownloadStatus.Completed);

            // Act
            var stats = await service.GetStatisticsAsync();

            // Assert
            Assert.Equal(3_500_000, stats.TotalBytesDownloaded);
        }

        [Fact]
        public async Task GetStatisticsAsync_LastActivity_UpdatedOnOperation()
        {
            // Arrange
            var service = new MemoryQueueService();
            var initialStats = await service.GetStatisticsAsync();
            var initialActivity = initialStats.LastActivity;

            // Act - wait a bit to ensure time difference
            await Task.Delay(10);
            await service.EnqueueAsync(CreateTestItem());
            var updatedStats = await service.GetStatisticsAsync();

            // Assert
            Assert.True(updatedStats.LastActivity > initialActivity);
        }

        #endregion

        #region Queue State (Pause/Resume)

        [Fact]
        public async Task IsQueuePausedAsync_InitialState_ReturnsFalse()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act
            var isPaused = await service.IsQueuePausedAsync();

            // Assert
            Assert.False(isPaused);
        }

        [Fact]
        public async Task SetQueueStateAsync_Pause_SetsPausedState()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act
            await service.SetQueueStateAsync(true);
            var isPaused = await service.IsQueuePausedAsync();

            // Assert
            Assert.True(isPaused);
        }

        [Fact]
        public async Task SetQueueStateAsync_Resume_SetsRunningState()
        {
            // Arrange
            var service = new MemoryQueueService();
            await service.SetQueueStateAsync(true);

            // Act
            await service.SetQueueStateAsync(false);
            var isPaused = await service.IsQueuePausedAsync();

            // Assert
            Assert.False(isPaused);
        }

        [Fact]
        public async Task SetQueueStateAsync_TogglePauses_TogglesCorrectly()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act & Assert - Multiple toggles
            Assert.False(await service.IsQueuePausedAsync());

            await service.SetQueueStateAsync(true);
            Assert.True(await service.IsQueuePausedAsync());

            await service.SetQueueStateAsync(false);
            Assert.False(await service.IsQueuePausedAsync());

            await service.SetQueueStateAsync(true);
            Assert.True(await service.IsQueuePausedAsync());
        }

        #endregion

        #region Thread Safety Tests

        [Fact]
        public async Task EnqueueAsync_ConcurrentEnqueues_AllItemsAdded()
        {
            // Arrange
            var service = new MemoryQueueService();
            const int threadCount = 50;
            var ids = new HashSet<string>();

            // Act
            var tasks = Enumerable.Range(0, threadCount).Select(i =>
                service.EnqueueAsync(CreateTestItem(title: $"Item {i}"))
            ).ToArray();

            var results = await Task.WhenAll(tasks);

            // Assert
            Assert.Equal(threadCount, results.Length);
            Assert.Equal(threadCount, ids.Count);
            Assert.All(results, id => Assert.NotEmpty(id));

            var queue = await service.GetQueueAsync();
            Assert.Equal(threadCount, queue.Count);
        }

        [Fact]
        public async Task UpdateItemAsync_ConcurrentUpdates_AllUpdatesApplied()
        {
            // Arrange
            var service = new MemoryQueueService();
            var itemId = await service.EnqueueAsync(CreateTestItem());

            // Act
            var tasks = Enumerable.Range(0, 20).Select(i =>
                service.UpdateItemAsync(itemId, DownloadStatus.Downloading, i * 5)
            ).ToArray();

            await Task.WhenAll(tasks);

            // Assert
            var item = await service.GetItemAsync(itemId);
            // One of the updates should have been applied
            Assert.True(item.ProgressPercent >= 0 && item.ProgressPercent <= 100);
            Assert.Equal(DownloadStatus.Downloading, item.Status);
        }

        [Fact]
        public async Task RemoveItemAsync_ConcurrentRemovesAndGets_NoExceptions()
        {
            // Arrange
            var service = new MemoryQueueService();
            var ids = new List<string>();

            for (int i = 0; i < 20; i++)
            {
                ids.Add(await service.EnqueueAsync(CreateTestItem(title: $"Item {i}")));
            }

            // Act - Concurrent removes and gets
            var removeTasks = ids.Take(10).Select(id => service.RemoveItemAsync(id));
            var getTasks = ids.Select(id => service.TryGetItemAsync(id));

            await Task.WhenAll(removeTasks);
            await Task.WhenAll(getTasks);

            // Assert - Should complete without throwing
            var queue = await service.GetQueueAsync();
            Assert.Equal(10, queue.Count);
        }

        [Fact]
        public async Task ConcurrentOperations_MixedOperations_ThreadSafe()
        {
            // Arrange
            var service = new MemoryQueueService();
            var id = await service.EnqueueAsync(CreateTestItem());

            // Act - Mix different operations
            var tasks = new List<Task>
            {
                // Multiple enqueue operations
                service.EnqueueAsync(CreateTestItem(title: "A")),
                service.EnqueueAsync(CreateTestItem(title: "B")),
                service.EnqueueAsync(CreateTestItem(title: "C")),

                // Update operations
                service.UpdateItemAsync(id, DownloadStatus.Downloading, 25),
                service.UpdateItemAsync(id, DownloadStatus.Downloading, 50),
                service.UpdateItemAsync(id, DownloadStatus.Downloading, 75),

                // Get operations
                service.GetItemAsync(id),
                service.GetQueueAsync(),
                service.GetStatisticsAsync(),

                // State operations
                service.SetQueueStateAsync(true),
                service.SetQueueStateAsync(false)
            };

            // Assert - Should complete without throwing
            await Task.WhenAll(tasks);

            var queue = await service.GetQueueAsync();
            Assert.True(queue.Count >= 1);
        }

        [Fact]
        public async Task EnqueueAsync_ConcurrentEnqueues_GeneratesUniqueIds()
        {
            // Arrange
            var service = new MemoryQueueService();
            const int itemCount = 100;

            // Act
            var tasks = Enumerable.Range(0, itemCount).Select(_ =>
                service.EnqueueAsync(CreateTestItem())
            ).ToArray();

            var results = await Task.WhenAll(tasks);

            // Assert
            var uniqueIds = new HashSet<string>(results);
            Assert.Equal(itemCount, uniqueIds.Count);
        }

        #endregion

        #region Empty Queue Behavior

        [Fact]
        public async Task GetQueueAsync_EmptyQueue_ReturnsEmptyListNotDefault()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act
            var queue = await service.GetQueueAsync();

            // Assert
            Assert.NotNull(queue);
            Assert.Empty(queue);
            Assert.IsType<List<CliDownloadItem>>(queue);
        }

        [Fact]
        public async Task RemoveItemAsync_FromEmptyQueue_DoesNotThrow()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act & Assert
            await service.RemoveItemAsync("any-id");
            var queue = await service.GetQueueAsync();
            Assert.Empty(queue);
        }

        [Fact]
        public async Task UpdateItemAsync_OnEmptyQueue_DoesNotThrow()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act & Assert
            await service.UpdateItemAsync("any-id", DownloadStatus.Completed);
        }

        [Fact]
        public async Task ClearCompletedAsync_OnEmptyQueue_DoesNotThrow()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act & Assert
            await service.ClearCompletedAsync();
            var queue = await service.GetQueueAsync();
            Assert.Empty(queue);
        }

        [Fact]
        public async Task GetStatisticsAsync_OnEmptyQueue_ReturnsEmptyStats()
        {
            // Arrange
            var service = new MemoryQueueService();

            // Act
            var stats = await service.GetStatisticsAsync();

            // Assert
            Assert.NotNull(stats);
            Assert.Equal(0, stats.TotalItems);
            Assert.Equal(0, stats.CompletedItems);
            Assert.Equal(0, stats.FailedItems);
            Assert.Equal(0, stats.InProgressItems);
            Assert.Equal(0, stats.PendingItems);
            Assert.Equal(0, stats.TotalBytesDownloaded);
        }

        #endregion

        #region Helper Methods

        private static CliDownloadItem CreateTestItem(
            string title = "Test Song",
            string artist = "Test Artist",
            string albumId = "test-album-123")
        {
            return new CliDownloadItem
            {
                Title = title,
                Artist = artist,
                AlbumId = albumId,
                OutputPath = $"/tmp/downloads/{artist}/",
                TotalBytes = 10_000_000,
                Quality = "FLAC",
                Format = "FLAC"
            };
        }

        #endregion
    }
}
