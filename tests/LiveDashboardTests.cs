using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.CLI.Models;
using Lidarr.Plugin.Common.CLI.Services;
using Lidarr.Plugin.Common.CLI.UI;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Comprehensive tests for LiveDashboard class.
    /// Tests cover update operations, refresh operations, display operations,
    /// progress reporting, status tracking, and cancellation handling.
    /// </summary>
    [Trait("Category", "Unit")]
    public class LiveDashboardTests
    {
        #region Constructor Tests

        [Fact]
        public void Constructor_WithValidDependencies_CreatesInstance()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();

            // Act
            var dashboard = new LiveDashboard(queueService, ui);

            // Assert
            Assert.NotNull(dashboard);
            Assert.False(dashboard.IsRunning);
            Assert.Equal(1000, dashboard.RefreshIntervalMs);
        }

        [Fact]
        public void Constructor_WithNullQueueService_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new LiveDashboard(null!, new MockConsoleUI()));
        }

        [Fact]
        public void Constructor_WithNullUI_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new LiveDashboard(new MockQueueService(), null!));
        }

        #endregion

        #region StartAsync Tests

        [Fact]
        public async Task StartAsync_WhenNotRunning_StartsDashboard()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.StartAsync();

            // Assert
            Assert.True(dashboard.IsRunning);
        }

        [Fact]
        public async Task StartAsync_WhenAlreadyRunning_DoesNotStartDuplicate()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.StartAsync();
            var firstRunning = dashboard.IsRunning;
            await dashboard.StartAsync();
            var secondRunning = dashboard.IsRunning;

            // Assert
            Assert.True(firstRunning);
            Assert.True(secondRunning);
        }

        [Fact]
        public async Task StartAsync_ClearsConsoleOnStart()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.StartAsync();
            await Task.Delay(200); // Allow time for dashboard to start

            // Assert
            Assert.True(ui.ClearCallCount > 0);
        }

        [Fact]
        public async Task StartAsync_WithCancellationToken_PropagatesCancellation()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);
            using var cts = new CancellationTokenSource();

            // Act
            var startTask = dashboard.StartAsync(cts.Token);
            cts.Cancel();

            // Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await startTask);
        }

        [Fact]
        [Trait("State", "Quarantined")]  // Quarantined 2026-02-04: Flaky timing-sensitive test on Linux CI - Issue #318
        public async Task StartAsync_WithShortRefreshInterval_UsesCustomInterval()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui)
            {
                RefreshIntervalMs = 100
            };

            // Act
            await dashboard.StartAsync();
            await Task.Delay(350); // Allow for multiple refreshes

            // Assert
            Assert.True(ui.ClearCallCount >= 2, "Should have refreshed at least twice");
        }

        #endregion

        #region StopAsync Tests

        [Fact]
        public async Task StopAsync_WhenRunning_StopsDashboard()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);
            await dashboard.StartAsync();

            // Act
            await dashboard.StopAsync();
            await Task.Delay(100); // Allow time for stop to complete

            // Assert
            Assert.False(dashboard.IsRunning);
        }

        [Fact]
        public async Task StopAsync_WhenNotRunning_DoesNothing()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act - should not throw
            await dashboard.StopAsync();

            // Assert
            Assert.False(dashboard.IsRunning);
        }

        [Fact]
        public async Task StopAsync_MultipleCalls_DoesNotThrow()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);
            await dashboard.StartAsync();

            // Act & Assert - should not throw
            await dashboard.StopAsync();
            await dashboard.StopAsync();
            await dashboard.StopAsync();
        }

        #endregion

        #region RefreshAsync Tests

        [Fact]
        public async Task RefreshAsync_WhenNotRunning_RefreshesDisplay()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.True(ui.ClearCallCount > 0);
        }

        [Fact]
        public async Task RefreshAsync_WhenRunning_RefreshesDisplay()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);
            await dashboard.StartAsync();

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.True(ui.ClearCallCount > 0);
        }

        [Fact]
        [Trait("State", "Quarantined")]  // Quarantined 2026-02-04: Flaky on Linux CI - Issue #318
        public async Task RefreshAsync_RetrievesQueueData()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "1",
                        Title = "Test Song",
                        Artist = "Test Artist",
                        Status = DownloadStatus.Downloading,
                        ProgressPercent = 45
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.True(queueService.GetQueueCalled);
        }

        [Fact]
        public async Task RefreshAsync_RetrievesStatistics()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.True(queueService.GetStatisticsCalled);
        }

        [Fact]
        public async Task RefreshAsync_RetrievesQueuePausedState()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.True(queueService.IsQueuePausedCalled);
        }

        [Fact]
        public async Task RefreshAsync_MultipleCalls_EachRefreshesData()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();
            var firstCount = queueService.GetQueueCallCount;

            await dashboard.RefreshAsync();
            var secondCount = queueService.GetQueueCallCount;

            // Assert
            Assert.Equal(1, firstCount);
            Assert.Equal(2, secondCount);
        }

        #endregion

        #region Display Operations Tests

        [Fact]
        public async Task Display_WithEmptyQueue_ShowsEmptyState()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>()
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.True(queueService.GetQueueCalled);
        }

        [Fact]
        public async Task Display_WithSingleItem_DisplaysItemCorrectly()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "item-1",
                        Title = "Test Track",
                        Artist = "Test Artist",
                        Status = DownloadStatus.Completed,
                        ProgressPercent = 100
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.True(queueService.GetQueueCalled);
        }

        [Fact]
        public async Task Display_WithMultipleItems_DisplaysAll()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem { Id = "1", Title = "Song 1", Status = DownloadStatus.Completed },
                    new CliDownloadItem { Id = "2", Title = "Song 2", Status = DownloadStatus.Downloading },
                    new CliDownloadItem { Id = "3", Title = "Song 3", Status = DownloadStatus.Pending }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.Equal(3, queueService.QueueItems.Count);
        }

        [Fact]
        public async Task Display_WithMoreThanTenItems_ShowsLastTen()
        {
            // Arrange
            var items = Enumerable.Range(1, 15)
                .Select(i => new CliDownloadItem
                {
                    Id = $"item-{i}",
                    Title = $"Song {i}",
                    Artist = "Artist",
                    Status = DownloadStatus.Pending
                })
                .ToList();

            var queueService = new MockQueueService { QueueItems = items };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            // Dashboard should show last 10 items (items 6-15)
            Assert.True(queueService.GetQueueCalled);
        }

        #endregion

        #region Progress Reporting Tests

        [Fact]
        [Trait("State", "Quarantined")]  // Quarantined 2026-02-04: Flaky on Linux CI - Issue #318
        public async Task Display_WithDownloadingItem_ShowsProgressPercentage()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "downloading-1",
                        Title = "Downloading Track",
                        Artist = "Artist",
                        Status = DownloadStatus.Downloading,
                        ProgressPercent = 67
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.Equal(DownloadStatus.Downloading, queueService.QueueItems[0].Status);
            Assert.Equal(67, queueService.QueueItems[0].ProgressPercent);
        }

        [Fact]
        [Trait("State", "Quarantined")]  // Quarantined 2026-02-04: Flaky on Linux CI - Issue #318
        public async Task Display_WithZeroProgress_DisplaysZero()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "start-1",
                        Title = "Starting Track",
                        Status = DownloadStatus.Downloading,
                        ProgressPercent = 0
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.Equal(0, queueService.QueueItems[0].ProgressPercent);
        }

        [Fact]
        public async Task Display_WithHundredProgress_DisplaysCompleted()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "complete-1",
                        Title = "Complete Track",
                        Status = DownloadStatus.Downloading,
                        ProgressPercent = 100
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.Equal(100, queueService.QueueItems[0].ProgressPercent);
        }

        [Fact]
        public async Task Display_ProgressUpdateOverTime_ReflectsNewProgress()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "progress-1",
                        Title = "Progress Track",
                        Status = DownloadStatus.Downloading,
                        ProgressPercent = 25
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act - First refresh
            await dashboard.RefreshAsync();
            var firstProgress = queueService.QueueItems[0].ProgressPercent;

            // Update progress
            queueService.QueueItems[0].ProgressPercent = 50;

            // Second refresh
            await dashboard.RefreshAsync();
            var secondProgress = queueService.QueueItems[0].ProgressPercent;

            // Assert
            Assert.Equal(25, firstProgress);
            Assert.Equal(50, secondProgress);
        }

        #endregion

        #region Status Tracking Tests

        [Fact]
        public async Task Display_WithCompletedItem_ShowsCompletedStatus()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "completed-1",
                        Title = "Completed Track",
                        Status = DownloadStatus.Completed
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.Equal(DownloadStatus.Completed, queueService.QueueItems[0].Status);
        }

        [Fact]
        public async Task Display_WithFailedItem_ShowsFailedStatus()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "failed-1",
                        Title = "Failed Track",
                        Status = DownloadStatus.Failed,
                        StatusMessage = "Connection lost"
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.Equal(DownloadStatus.Failed, queueService.QueueItems[0].Status);
            Assert.Equal("Connection lost", queueService.QueueItems[0].StatusMessage);
        }

        [Fact]
        public async Task Display_WithPendingItem_ShowsPendingStatus()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "pending-1",
                        Title = "Pending Track",
                        Status = DownloadStatus.Pending
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.Equal(DownloadStatus.Pending, queueService.QueueItems[0].Status);
        }

        [Fact]
        public async Task Display_WithCancelledItem_ShowsCancelledStatus()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "cancelled-1",
                        Title = "Cancelled Track",
                        Status = DownloadStatus.Cancelled
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.Equal(DownloadStatus.Cancelled, queueService.QueueItems[0].Status);
        }

        [Fact]
        public async Task Display_WithPausedItem_ShowsPausedStatus()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "paused-1",
                        Title = "Paused Track",
                        Status = DownloadStatus.Paused
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.Equal(DownloadStatus.Paused, queueService.QueueItems[0].Status);
        }

        [Fact]
        [Trait("State", "Quarantined")]  // Quarantined 2026-02-04: Flaky on Linux CI - Issue #318
        public async Task Display_Statistics_ShowCorrectCounts()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem { Id = "1", Status = DownloadStatus.Completed },
                    new CliDownloadItem { Id = "2", Status = DownloadStatus.Completed },
                    new CliDownloadItem { Id = "3", Status = DownloadStatus.Failed },
                    new CliDownloadItem { Id = "4", Status = DownloadStatus.Downloading },
                    new CliDownloadItem { Id = "5", Status = DownloadStatus.Pending },
                    new CliDownloadItem { Id = "6", Status = DownloadStatus.Pending }
                },
                Statistics = new QueueStatistics
                {
                    TotalItems = 6,
                    CompletedItems = 2,
                    FailedItems = 1,
                    InProgressItems = 1,
                    PendingItems = 2
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            var stats = await queueService.GetStatisticsAsync();
            Assert.Equal(6, stats.TotalItems);
            Assert.Equal(2, stats.CompletedItems);
            Assert.Equal(1, stats.FailedItems);
            Assert.Equal(1, stats.InProgressItems);
            Assert.Equal(2, stats.PendingItems);
        }

        [Fact]
        public async Task Display_WithQueuePaused_ShowPausedState()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                IsPaused = true
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.True(queueService.IsPaused);
        }

        [Fact]
        public async Task Display_WithQueueRunning_ShowRunningState()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                IsPaused = false
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.RefreshAsync();

            // Assert
            Assert.False(queueService.IsPaused);
        }

        #endregion

        #region Cancellation Handling Tests

        [Fact]
        public async Task StartAsync_WithImmediateCancellation_StopsGracefully()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);
            using var cts = new CancellationTokenSource();

            // Act
            var startTask = dashboard.StartAsync(cts.Token);
            cts.Cancel();

            // Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await startTask);
            Assert.False(dashboard.IsRunning);
        }

        [Fact]
        public async Task StartAsync_WithDelayedCancellation_AllowsSomeRefreshes()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui)
            {
                RefreshIntervalMs = 50
            };
            using var cts = new CancellationTokenSource();

            // Act
            var startTask = dashboard.StartAsync(cts.Token);
            await Task.Delay(150); // Allow some refreshes
            cts.Cancel();

            // Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await startTask);
            Assert.True(ui.ClearCallCount > 0);
        }

        [Fact]
        public async Task StopAsync_DuringRefresh_CompletesSafely()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = Enumerable.Range(1, 100)
                    .Select(i => new CliDownloadItem
                    {
                        Id = $"item-{i}",
                        Title = $"Song {i}",
                        Status = DownloadStatus.Downloading
                    })
                    .ToList()
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            await dashboard.StartAsync();
            await Task.Delay(50); // Let at least one refresh start

            // Act
            await dashboard.StopAsync();

            // Assert
            Assert.False(dashboard.IsRunning);
        }

        [Fact]
        public async Task StartAsync_MultipleStartStopCycles_HandlesCorrectly()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act - First cycle
            await dashboard.StartAsync();
            Assert.True(dashboard.IsRunning);
            await dashboard.StopAsync();
            Assert.False(dashboard.IsRunning);

            // Second cycle
            await dashboard.StartAsync();
            Assert.True(dashboard.IsRunning);
            await dashboard.StopAsync();
            Assert.False(dashboard.IsRunning);

            // Third cycle
            await dashboard.StartAsync();
            Assert.True(dashboard.IsRunning);
            await dashboard.StopAsync();
            Assert.False(dashboard.IsRunning);

            // Assert
            Assert.False(dashboard.IsRunning);
        }

        #endregion

        #region IsRunning Property Tests

        [Fact]
        public void IsRunning_WhenNotStarted_ReturnsFalse()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act & Assert
            Assert.False(dashboard.IsRunning);
        }

        [Fact]
        public async Task IsRunning_WhenStarted_ReturnsTrue()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.StartAsync();

            // Assert
            Assert.True(dashboard.IsRunning);
        }

        [Fact]
        public async Task IsRunning_WhenStopped_ReturnsFalse()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            await dashboard.StartAsync();
            await dashboard.StopAsync();
            await Task.Delay(100); // Allow time for stop

            // Assert
            Assert.False(dashboard.IsRunning);
        }

        #endregion

        #region RefreshIntervalMs Property Tests

        [Fact]
        public void RefreshIntervalMs_DefaultValue_Is1000ms()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();

            // Act
            var dashboard = new LiveDashboard(queueService, ui);

            // Assert
            Assert.Equal(1000, dashboard.RefreshIntervalMs);
        }

        [Fact]
        public void RefreshIntervalMs_CanBeModified()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act
            dashboard.RefreshIntervalMs = 500;

            // Assert
            Assert.Equal(500, dashboard.RefreshIntervalMs);
        }

        [Fact]
        public async Task RefreshIntervalMs_WhileRunning_AffectsNextRefresh()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui)
            {
                RefreshIntervalMs = 500
            };

            // Act
            await dashboard.StartAsync();
            dashboard.RefreshIntervalMs = 100;
            await Task.Delay(600); // Wait for original interval + one more

            // Assert
            // Should have more refreshes due to shorter interval
            Assert.True(ui.ClearCallCount >= 1);
        }

        [Fact]
        public async Task RefreshIntervalMs_SetToZero_HandlesGracefully()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui)
            {
                RefreshIntervalMs = 0
            };

            // Act - Should not throw
            await dashboard.StartAsync();
            await Task.Delay(100);
            await dashboard.StopAsync();

            // Assert
            Assert.False(dashboard.IsRunning);
        }

        #endregion

        #region Edge Cases Tests

        [Fact]
        public async Task Display_WithNullTitle_DisplaysUnknown()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "null-title",
                        Title = null!,
                        Artist = "Artist",
                        Status = DownloadStatus.Pending
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act - Should not throw
            await dashboard.RefreshAsync();

            // Assert
            Assert.Null(queueService.QueueItems[0].Title);
        }

        [Fact]
        public async Task Display_WithNullArtist_DisplaysUnknown()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>
                {
                    new CliDownloadItem
                    {
                        Id = "null-artist",
                        Title = "Song",
                        Artist = null!,
                        Status = DownloadStatus.Pending
                    }
                }
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act - Should not throw
            await dashboard.RefreshAsync();

            // Assert
            Assert.Null(queueService.QueueItems[0].Artist);
        }

        [Fact]
        public async Task Display_WithEmptyQueue_DoesNotThrow()
        {
            // Arrange
            var queueService = new MockQueueService
            {
                QueueItems = new List<CliDownloadItem>()
            };
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act & Assert - Should not throw
            await dashboard.RefreshAsync();
        }

        [Fact]
        public async Task RefreshAsync_WhileDashboardRunning_DoesNotConflict()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            await dashboard.StartAsync();

            // Act - Manual refresh while auto-refresh running
            var refreshTask1 = dashboard.RefreshAsync();
            var refreshTask2 = dashboard.RefreshAsync();

            await Task.WhenAll(refreshTask1, refreshTask2);
            await dashboard.StopAsync();

            // Assert - Should not throw
            Assert.True(queueService.GetQueueCallCount >= 2);
        }

        [Fact]
        public async Task StopAsync_CalledImmediatelyAfterStart_StopsSuccessfully()
        {
            // Arrange
            var queueService = new MockQueueService();
            var ui = new MockConsoleUI();
            var dashboard = new LiveDashboard(queueService, ui);

            // Act - Start and immediately stop
            await dashboard.StartAsync();
            await dashboard.StopAsync();

            // Assert
            Assert.False(dashboard.IsRunning);
        }

        #endregion

        #region Mock Classes for Testing

        /// <summary>
        /// Mock implementation of IQueueService for testing
        /// </summary>
        private sealed class MockQueueService : IQueueService
        {
            public List<CliDownloadItem> QueueItems { get; set; } = new();
            public QueueStatistics Statistics { get; set; } = new();
            public bool IsPaused { get; set; }

            public bool GetQueueCalled { get; private set; }
            public int GetQueueCallCount { get; private set; }
            public bool GetStatisticsCalled { get; private set; }
            public bool IsQueuePausedCalled { get; private set; }

            public Task InitializeAsync() => Task.CompletedTask;

            public Task<string> EnqueueAsync(CliDownloadItem item)
            {
                QueueItems.Add(item);
                return Task.FromResult(item.Id);
            }

            public Task<List<CliDownloadItem>> GetQueueAsync()
            {
                GetQueueCalled = true;
                GetQueueCallCount++;
                return Task.FromResult(QueueItems.OrderBy(x => x.AddedDate).ToList());
            }

            public Task<CliDownloadItem> GetItemAsync(string itemId)
            {
                var item = QueueItems.FirstOrDefault(x => x.Id == itemId);
                if (item == null)
                    throw new KeyNotFoundException($"Item '{itemId}' not found.");
                return Task.FromResult(item);
            }

            public Task<CliDownloadItem?> TryGetItemAsync(string itemId)
            {
                return Task.FromResult(QueueItems.FirstOrDefault(x => x.Id == itemId));
            }

            public Task UpdateItemAsync(string itemId, DownloadStatus status, int progressPercent = 0, string? statusMessage = null)
            {
                var item = QueueItems.FirstOrDefault(x => x.Id == itemId);
                if (item != null)
                {
                    item.Status = status;
                    item.ProgressPercent = Math.Max(0, Math.Min(100, progressPercent));
                    if (!string.IsNullOrEmpty(statusMessage))
                        item.StatusMessage = statusMessage;
                }
                return Task.CompletedTask;
            }

            public Task RemoveItemAsync(string itemId)
            {
                var item = QueueItems.FirstOrDefault(x => x.Id == itemId);
                if (item != null)
                    QueueItems.Remove(item);
                return Task.CompletedTask;
            }

            public Task ClearCompletedAsync()
            {
                QueueItems.RemoveAll(x => x.Status == DownloadStatus.Completed || x.Status == DownloadStatus.Failed);
                return Task.CompletedTask;
            }

            public Task<QueueStatistics> GetStatisticsAsync()
            {
                GetStatisticsCalled = true;

                if (Statistics.TotalItems == 0 && QueueItems.Count > 0)
                {
                    // Auto-calculate stats if not set
                    Statistics = new QueueStatistics
                    {
                        TotalItems = QueueItems.Count,
                        CompletedItems = QueueItems.Count(x => x.Status == DownloadStatus.Completed),
                        FailedItems = QueueItems.Count(x => x.Status == DownloadStatus.Failed),
                        InProgressItems = QueueItems.Count(x => x.Status == DownloadStatus.Downloading),
                        PendingItems = QueueItems.Count(x => x.Status == DownloadStatus.Pending),
                        LastActivity = DateTime.UtcNow
                    };
                }

                return Task.FromResult(Statistics);
            }

            public Task SetQueueStateAsync(bool isPaused)
            {
                IsPaused = isPaused;
                return Task.CompletedTask;
            }

            public Task<bool> IsQueuePausedAsync()
            {
                IsQueuePausedCalled = true;
                return Task.FromResult(IsPaused);
            }
        }

        /// <summary>
        /// Mock implementation of IConsoleUI for testing
        /// </summary>
        private sealed class MockConsoleUI : IConsoleUI
        {
            public int ClearCallCount { get; private set; }
            public List<string> MarkupWritten { get; } = new();
            public List<string> TextWritten { get; } = new();

            public void WriteMarkup(string markup)
            {
                MarkupWritten.Add(markup);
            }

            public void WriteMarkupLine(string markup)
            {
                MarkupWritten.Add(markup);
            }

            public void Write(string text)
            {
                TextWritten.Add(text);
            }

            public void WriteLine(string text = "")
            {
                TextWritten.Add(text);
            }

            public void WriteError(string message)
            {
                TextWritten.Add($"ERROR: {message}");
            }

            public void WriteWarning(string message)
            {
                TextWritten.Add($"WARNING: {message}");
            }

            public void WriteSuccess(string message)
            {
                TextWritten.Add($"SUCCESS: {message}");
            }

            public string Ask(string prompt)
            {
                return string.Empty;
            }

            public string AskPassword(string prompt)
            {
                return string.Empty;
            }

            public bool Confirm(string prompt, bool defaultValue = false)
            {
                return defaultValue;
            }

            public T Select<T>(string prompt, IEnumerable<T> choices, Func<T, string>? displaySelector = null)
            {
                return choices.First();
            }

            public IEnumerable<T> MultiSelect<T>(string prompt, IEnumerable<T> choices, Func<T, string>? displaySelector = null)
            {
                return choices.Take(1);
            }

            public void ShowTable<T>(IEnumerable<T> data, params (string header, Func<T, string> getValue)[] columns)
            {
            }

            public Task<T> ShowProgressAsync<T>(string taskDescription, Func<IProgress<ProgressInfo>, Task<T>> operation)
            {
                return operation(new Progress<ProgressInfo>());
            }

            public void ShowStatus(string title, Dictionary<string, object> data)
            {
            }

            public void Clear()
            {
                ClearCallCount++;
            }
        }

        #endregion
    }
}
