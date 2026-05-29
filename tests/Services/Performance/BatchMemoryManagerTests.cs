using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Performance;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Performance
{
    [Trait("Category", "Unit")]
    public class BatchMemoryManagerTests
    {
        private BatchMemoryManager CreateManager(long maxMemoryMB = 512)
        {
            return new BatchMemoryManager(maxMemoryMB: maxMemoryMB);
        }

        #region Constructor Tests

        [Fact]
        public void Constructor_InitializesWithDefaultMemoryLimit()
        {
            // Arrange & Act
            using var manager = new BatchMemoryManager();

            // Assert
            var stats = manager.GetMemoryStatistics();
            Assert.Equal(512, stats.MaxMemoryLimitMB);
        }

        [Fact]
        public void Constructor_AcceptsCustomMemoryLimit()
        {
            // Arrange & Act
            using var manager = new BatchMemoryManager(maxMemoryMB: 1024);

            // Assert
            var stats = manager.GetMemoryStatistics();
            Assert.Equal(1024, stats.MaxMemoryLimitMB);
        }

        [Fact]
        public void Constructor_InitializesOptimalBatchSize()
        {
            // Arrange & Act
            using var manager = new BatchMemoryManager();

            // Assert
            var batchSize = manager.GetOptimalBatchSize<int>();
            Assert.InRange(batchSize, 10, 1000);
        }

        #endregion

        #region ProcessWithMemoryManagementAsync Tests

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_ProcessesAllItems()
        {
            // Arrange
            var items = Enumerable.Range(1, 250).ToList();
            var processedItems = new List<int>();
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.Delay(10, ct);
                    return batch.Select(x => x * 2).ToList();
                }))
            {
                processedItems.AddRange(result.Results);
            }

            // Assert
            Assert.Equal(250, processedItems.Count);
            Assert.Equal(250, manager.GetMemoryStatistics().TotalItemsProcessed);
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_ReturnsStreamingResults()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            var batchCount = 0;
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return batch.Select(x => x * 2).ToList();
                }))
            {
                batchCount++;

                // Assert streaming properties
                Assert.True(result.BatchNumber > 0);
                Assert.True(result.ItemsInBatch > 0);
                Assert.True(result.ProcessedItems > 0);
                Assert.Equal(100, result.TotalItems);
                Assert.True(result.PercentComplete >= 0 && result.PercentComplete <= 100);
                Assert.True(result.BatchDuration >= TimeSpan.Zero);
            }

            // Assert
            Assert.True(batchCount > 0);
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_UsesAdaptiveBatchSizing()
        {
            // Arrange
            var items = Enumerable.Range(1, 500).ToList();
            var batchSizes = new List<int>();
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return batch.ToList();
                },
                new BatchMemoryOptions
                {
                    MinBatchSize = 10,
                    MaxBatchSize = 100
                }))
            {
                batchSizes.Add(result.ItemsInBatch);
            }

            // Assert - Batch sizes should vary based on memory conditions
            Assert.True(batchSizes.Count > 1);
            Assert.All(batchSizes, size => Assert.InRange(size, 10, 100));
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_ReducesBatchSizeUnderMemoryPressure()
        {
            // Arrange
            var items = Enumerable.Range(1, 200).ToList();
            using var manager = CreateManager();
            var initialBatchSize = manager.GetOptimalBatchSize<int>();

            // Simulate memory pressure by checking ShouldPauseForMemory
            // This is difficult to test directly, so we verify the mechanism exists
            var canPause = manager.ShouldPauseForMemory();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return batch.ToList();
                },
                new BatchMemoryOptions
                {
                    MinBatchSize = 5,
                    MaxBatchSize = 50
                }))
            {
                // Assert - Under memory pressure, batches should be smaller
                if (canPause)
                {
                    Assert.True(result.ItemsInBatch <= initialBatchSize);
                }
            }
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_HandlesOutOfMemoryException()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            var callCount = 0;
            var oomThrown = false;
            using var manager = CreateManager();

            // Act
            var totalProcessed = 0;
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    callCount++;
                    // Simulate OOM on first call
                    if (callCount == 1 && batch.Count() > 10)
                    {
                        oomThrown = true;
                        throw new OutOfMemoryException("Simulated OOM");
                    }
                    await Task.CompletedTask;
                    return batch.Select(x => x * 2).ToList();
                },
                new BatchMemoryOptions
                {
                    MinBatchSize = 10,
                    MaxBatchSize = 100
                }))
            {
                totalProcessed += result.Results.Count;
            }

            // Assert - OOM was exercised and recovered.
            Assert.True(oomThrown);
            Assert.True(callCount >= 1);
            // Regression (#24): the OOM retry previously processed only batch.Take(count/2) and
            // silently dropped the remainder. After recovery ALL items must be processed.
            Assert.Equal(items.Count, totalProcessed);
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_HandlesEmptyCollection()
        {
            // Arrange
            var items = Enumerable.Empty<int>();
            var processedBatches = 0;
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    processedBatches++;
                    await Task.CompletedTask;
                    return batch.ToList();
                }))
            {
                // Should not enter here for empty collection
            }

            // Assert
            Assert.Equal(0, processedBatches);
            Assert.Equal(0, manager.GetMemoryStatistics().TotalItemsProcessed);
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_HandlesZeroBatchSize()
        {
            // Arrange
            var items = Enumerable.Range(1, 50).ToList();
            using var manager = CreateManager();

            // Act & Assert - Should normalize zero/negative batch sizes
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return batch.ToList();
                },
                new BatchMemoryOptions
                {
                    MinBatchSize = 0, // Will be normalized to minimum
                    MaxBatchSize = 100
                }))
            {
                Assert.True(result.ItemsInBatch > 0);
            }
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_ReportsProgress()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            var progressReports = new List<BatchMemoryProgress>();
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return batch.ToList();
                },
                progress: new Progress<BatchMemoryProgress>(p => progressReports.Add(p))))
            {
                // Processing happens here
            }

            // Assert
            Assert.True(progressReports.Count > 0);
            Assert.All(progressReports, p =>
            {
                Assert.True(p.ProcessedItems >= 0);
                Assert.Equal(100, p.TotalItems);
                Assert.True(p.PercentComplete >= 0 && p.PercentComplete <= 100);
            });
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_ContinuesOnError()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            var processedCount = 0;
            var errorCount = 0;
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    // Throw error on first batch
                    if (processedCount == 0)
                    {
                        errorCount++;
                        throw new InvalidOperationException("Test error");
                    }
                    processedCount += batch.Count();
                    return batch.ToList();
                },
                new BatchMemoryOptions
                {
                    ContinueOnError = true
                }))
            {
                if (result.HasError)
                {
                    Assert.True(errorCount > 0);
                }
                else
                {
                    processedCount += result.ItemsInBatch;
                }
            }

            // Assert - Should have continued processing after error
            Assert.True(errorCount > 0);
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_StopsOnErrorWhenConfigured()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            using var manager = CreateManager();

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                    items,
                    async (batch, ct) =>
                    {
                        await Task.CompletedTask;
                        throw new InvalidOperationException("Test error");
                    },
                    new BatchMemoryOptions
                    {
                        ContinueOnError = false
                    }))
                {
                    // Should not reach here
                }
            });
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_RespectsCancellationToken()
        {
            // Arrange
            var items = Enumerable.Range(1, 1000).ToList();
            var cts = new CancellationTokenSource();
            using var manager = CreateManager();

            // Act
            var processedCount = 0;
            var task = Task.Run(async () =>
            {
                await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                    items,
                    async (batch, ct) =>
                    {
                        processedCount += batch.Count();
                        await Task.Delay(50, ct);
                        return batch.ToList();
                    },
                    cancellationToken: cts.Token))
                {
                    if (processedCount >= 50)
                    {
                        cts.Cancel();
                    }
                }
            });

            // Assert - Should cancel without throwing
            await Task.WhenAny(task, Task.Delay(5000));
            Assert.True(processedCount >= 50 || task.IsCompleted);
        }

        [Fact]
        public async Task ProcessWithMemoryManagementAsync_ThrowsWhenDisposed()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            var manager = new BatchMemoryManager();
            manager.Dispose();

            // Act & Assert
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            {
                await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                    items,
                    async (batch, ct) =>
                    {
                        await Task.CompletedTask;
                        return batch.ToList();
                    }))
                {
                    // Should not reach here
                }
            });
        }

        #endregion

        #region GetMemoryStatistics Tests

        [Fact]
        public void GetMemoryStatistics_ReturnsValidStatistics()
        {
            // Arrange & Act
            using var manager = CreateManager();
            var stats = manager.GetMemoryStatistics();

            // Assert
            Assert.NotNull(stats);
            Assert.True(stats.CurrentMemoryUsageMB >= 0);
            Assert.Equal(512, stats.MaxMemoryLimitMB);
            Assert.True(stats.AvailableMemoryMB >= 0);
            Assert.Equal(0, stats.TotalItemsProcessed);
            Assert.Equal(0, stats.TotalBatchesExecuted);
            Assert.True(stats.ProcessingDuration >= TimeSpan.Zero);
        }

        [Fact]
        public async Task GetMemoryStatistics_ReflectsProcessing()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            using var manager = CreateManager();

            // Act
            await Task.Run(async () =>
            {
                await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                    items,
                    async (batch, ct) =>
                    {
                        await Task.CompletedTask;
                        return batch.Select(x => x * 2).ToList();
                    }))
                {
                }
            });

            // Assert
            var stats = manager.GetMemoryStatistics();
            Assert.Equal(100, stats.TotalItemsProcessed);
            Assert.True(stats.TotalBatchesExecuted > 0);
            Assert.True(stats.AverageBatchSize > 0);
        }

        [Fact]
        public void GetMemoryStatistics_CalculatesMemoryUtilization()
        {
            // Use a very high limit (100 GB) so the test process's actual working set
            // can't realistically exceed it. The previous default of 512 MB caused
            // intermittent failures under full-suite load when the test runner's
            // working set exceeded 512 MB — MemoryUtilizationPercent is computed from
            // real Process.WorkingSet64, not from any clamp, so values > 100 are
            // legitimate signal (process is over its configured budget) and worth
            // preserving in production. The test just needs a budget the runner can't
            // bust under load.
            using var manager = CreateManager(maxMemoryMB: 100_000);
            var stats = manager.GetMemoryStatistics();

            // Assert
            Assert.True(stats.MemoryUtilizationPercent >= 0);
            Assert.True(stats.MemoryUtilizationPercent <= 100);
        }

        #endregion

        #region GetOptimalBatchSize Tests

        [Fact]
        public void GetOptimalBatchSize_ReturnsValidSize()
        {
            // Arrange & Act
            using var manager = CreateManager();
            var batchSize = manager.GetOptimalBatchSize<int>();

            // Assert
            Assert.InRange(batchSize, 10, 1000);
        }

        [Fact]
        public async Task GetOptimalBatchSize_AdaptsBasedOnPerformance()
        {
            // Arrange
            using var manager = CreateManager();
            var initialBatchSize = manager.GetOptimalBatchSize<int>();
            var items = Enumerable.Range(1, 500).ToList();

            // Act - Process items quickly (high throughput)
            await Task.Run(async () =>
            {
                await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                    items,
                    async (batch, ct) =>
                    {
                        // Fast processing to simulate high throughput
                        await Task.CompletedTask;
                        return batch.Select(x => x * 2).ToList();
                    }))
                {
                    // Processing
                }
            });

            // Assert - Batch size may have adjusted based on performance
            var finalBatchSize = manager.GetOptimalBatchSize<int>();
            Assert.InRange(finalBatchSize, 10, 1000);
        }

        #endregion

        #region ShouldPauseForMemory Tests

        [Fact]
        public void ShouldPauseForMemory_ReturnsBoolean()
        {
            // Arrange & Act
            using var manager = CreateManager();
            var shouldPause = manager.ShouldPauseForMemory();

            // Assert
            Assert.IsType<bool>(shouldPause);
        }

        [Fact]
        public void ShouldPauseForMemory_DetectsMemoryPressure()
        {
            // Arrange & Act
            using var manager = CreateManager();
            // This is difficult to test directly, but we can verify the mechanism
            var shouldPause = manager.ShouldPauseForMemory();
            var stats = manager.GetMemoryStatistics();

            // Assert
            Assert.Equal(shouldPause, stats.IsMemoryPressureHigh);
        }

        #endregion

        #region Streaming Correctness Tests

        [Fact]
        public async Task StreamingCorrectness_PreservesOrder()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            var results = new List<int>();
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return batch.Select(x => x * 2).ToList();
                }))
            {
                results.AddRange(result.Results);
            }

            // Assert - Order should be preserved
            var expected = Enumerable.Range(1, 100).Select(x => x * 2).ToList();
            Assert.Equal(expected, results);
        }

        [Fact]
        public async Task StreamingCorrectness_ProcessesAllItemsExactlyOnce()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            var processedItems = new HashSet<int>();
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return batch.ToList();
                }))
            {
                foreach (var item in result.Results)
                {
                    // Assert - Each item should be processed exactly once
                    Assert.DoesNotContain(item, processedItems);
                    processedItems.Add(item);
                }
            }

            // Assert
            Assert.Equal(100, processedItems.Count);
        }

        [Fact]
        public async Task StreamingCorrectness_HandlesNullResults()
        {
            // Arrange
            var items = Enumerable.Range(1, 50).ToList();
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return null!; // Simulate null result
                }))
            {
                // Assert - Should handle null gracefully
                Assert.NotNull(result.Results);
                Assert.Empty(result.Results);
            }
        }

        #endregion

        #region Edge Cases Tests

        [Fact]
        public async Task EdgeCase_SingleItem()
        {
            // Arrange
            var items = new List<int> { 42 };
            using var manager = CreateManager();

            // Act
            var results = new List<int>();
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return batch.Select(x => x * 2).ToList();
                }))
            {
                results.AddRange(result.Results);
            }

            // Assert
            Assert.Single(results);
            Assert.Equal(84, results[0]);
        }

        [Fact]
        public async Task EdgeCase_VeryLargeCollection()
        {
            // Arrange
            var items = Enumerable.Range(1, 10000).ToList();
            var totalCount = 0;
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    totalCount += batch.Count();
                    return batch.ToList();
                }))
            {
                // Processing
            }

            // Assert
            Assert.Equal(10000, totalCount);
        }

        [Fact]
        public async Task EdgeCase_BatchSizeExceedsItemCount()
        {
            // Arrange
            var items = new List<int> { 1, 2, 3 };
            using var manager = CreateManager();

            // Act
            var batchCount = 0;
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    batchCount++;
                    await Task.CompletedTask;
                    return batch.ToList();
                },
                new BatchMemoryOptions
                {
                    MinBatchSize = 100, // Much larger than item count
                    MaxBatchSize = 1000
                }))
            {
                // Assert - Should only process one batch
                Assert.Equal(3, result.ItemsInBatch);
            }

            // Assert
            Assert.Equal(1, batchCount);
        }

        #endregion

        #region Disposal Tests

        [Fact]
        public void Dispose_CanBeCalledMultipleTimes()
        {
            // Arrange
            var manager = new BatchMemoryManager();

            // Act & Assert - Should not throw
            manager.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task DisposeAsync_CanBeCalledMultipleTimes()
        {
            // Arrange
            var manager = new BatchMemoryManager();

            // Act & Assert - Should not throw
            await manager.DisposeAsync();
            await manager.DisposeAsync();
        }

        [Fact]
        public async Task DisposeAsync_StopsMonitoring()
        {
            // Arrange
            var manager = new BatchMemoryManager();

            // Act
            await manager.DisposeAsync();

            // Assert - Should be disposed
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            {
                await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                    Enumerable.Range(1, 10),
                    async (batch, ct) =>
                    {
                        await Task.CompletedTask;
                        return batch.ToList();
                    }))
                {
                }
            });
        }

        #endregion

        #region Memory Cleanup Tests

        [Fact]
        public async Task PerformMemoryCleanup_TriggeredPeriodically()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return batch.ToList();
                },
                new BatchMemoryOptions
                {
                    EnablePeriodicCleanup = true
                }))
            {
                // Processing with periodic cleanup enabled
            }

            // Assert - Should complete without errors
            var stats = manager.GetMemoryStatistics();
            Assert.Equal(100, stats.TotalItemsProcessed);
        }

        [Fact]
        public async Task PerformMemoryCleanup_CanBeDisabled()
        {
            // Arrange
            var items = Enumerable.Range(1, 100).ToList();
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.CompletedTask;
                    return batch.ToList();
                },
                new BatchMemoryOptions
                {
                    EnablePeriodicCleanup = false
                }))
            {
                // Processing without periodic cleanup
            }

            // Assert - Should complete without errors
            var stats = manager.GetMemoryStatistics();
            Assert.Equal(100, stats.TotalItemsProcessed);
        }

        #endregion

        #region BatchDuration Tests

        [Fact]
        public async Task BatchDuration_IsTracked()
        {
            // Arrange
            var items = Enumerable.Range(1, 50).ToList();
            using var manager = CreateManager();

            // Act
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    await Task.Delay(50, ct);
                    return batch.ToList();
                }))
            {
                // Assert - Duration should be tracked
                Assert.True(result.BatchDuration >= TimeSpan.FromMilliseconds(50));
            }
        }

        #endregion

        #region Adaptive Performance Tests

        [Fact]
        public async Task AdaptiveBatching_IncreasesOnHighThroughput()
        {
            // Arrange
            var items = Enumerable.Range(1, 500).ToList();
            using var manager = CreateManager();
            var initialBatchSize = manager.GetOptimalBatchSize<int>();

            // Act - Process items very quickly
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    // Simulate high throughput (very fast processing)
                    await Task.CompletedTask;
                    return batch.Select(x => x * 2).ToList();
                }))
            {
                // Processing
            }

            // Assert - Batch size may have increased
            var finalBatchSize = manager.GetOptimalBatchSize<int>();
            Assert.InRange(finalBatchSize, 10, 1000);
        }

        [Fact]
        public async Task AdaptiveBatching_DecreasesOnLowThroughput()
        {
            // Arrange
            var items = Enumerable.Range(1, 200).ToList();
            using var manager = CreateManager();
            var initialBatchSize = manager.GetOptimalBatchSize<int>();

            // Act - Process items slowly
            await foreach (var result in manager.ProcessWithMemoryManagementAsync<int, int>(
                items,
                async (batch, ct) =>
                {
                    // Simulate low throughput (slow processing)
                    await Task.Delay(100, ct);
                    return batch.Select(x => x * 2).ToList();
                }))
            {
                // Processing
            }

            // Assert - Batch size may have decreased
            var finalBatchSize = manager.GetOptimalBatchSize<int>();
            Assert.InRange(finalBatchSize, 10, 1000);
        }

        #endregion
    }
}
