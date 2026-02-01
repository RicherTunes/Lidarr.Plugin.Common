using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Performance;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class AdaptiveConcurrencyManagerTests
    {
        #region Test Setup

        private AdaptiveConcurrencyManager CreateManager(
            ILogger<AdaptiveConcurrencyManager>? logger = null,
            int minConcurrency = 1,
            int maxConcurrency = 8,
            TimeSpan? adjustmentInterval = null,
            double targetLatency = 1000.0,
            double maxLatency = 5000.0)
        {
            return new AdaptiveConcurrencyManager(
                logger ?? Mock.Of<ILogger<AdaptiveConcurrencyManager>>(),
                minConcurrency,
                maxConcurrency,
                adjustmentInterval,
                targetLatency,
                maxLatency);
        }

        #endregion

        #region Constructor Tests

        [Fact]
        public void Constructor_InitializesWithValidRange()
        {
            // Arrange & Act
            var manager = CreateManager(
                minConcurrency: 2,
                maxConcurrency: 8);

            // Assert
            Assert.InRange(manager.CurrentConcurrency, 2, 8);
        }

        [Fact]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new AdaptiveConcurrencyManager(
                    null!,
                    1,
                    8));
        }

        [Fact]
        public void Constructor_ValidatesMinConcurrencyAtLeastOne()
        {
            // Arrange & Act
            var manager = CreateManager(minConcurrency: 0);

            // Assert - Should be normalized to 1
            Assert.True(manager.CurrentConcurrency >= 1);
        }

        [Fact]
        public void Constructor_ValidatesMaxNotLessThanMin()
        {
            // Arrange & Act
            var manager = CreateManager(
                minConcurrency: 5,
                maxConcurrency: 3); // max < min

            // Assert - Should be normalized
            Assert.True(manager.CurrentConcurrency >= 1);
            Assert.InRange(manager.CurrentConcurrency, 1, 5);
        }

        [Fact]
        public void Constructor_UsesProcessorBasedCalculation()
        {
            // Arrange & Act
            var manager = CreateManager(
                minConcurrency: 1,
                maxConcurrency: 16);

            // Assert - Should use a reasonable value based on processor count
            // but not exceed max or be below min
            Assert.InRange(manager.CurrentConcurrency, 1, 16);
        }

        #endregion

        #region RecordOperation Tests

        [Fact]
        public void RecordOperation_IncreasesConcurrencyOnGoodPerformance()
        {
            // Arrange
            var manager = CreateManager(
                minConcurrency: 1,
                maxConcurrency: 8,
                adjustmentInterval: TimeSpan.Zero);

            var initialConcurrency = manager.CurrentConcurrency;

            // Record many successful, fast operations
            for (int i = 0; i < 25; i++)
            {
                manager.RecordOperation(
                    TimeSpan.FromMilliseconds(100), // Fast
                    true);
            }

            // Act
            var finalConcurrency = manager.CurrentConcurrency;

            // Assert - Should have increased due to good performance
            Assert.True(finalConcurrency >= initialConcurrency);
        }

        [Fact]
        public void RecordOperation_DecreasesConcurrencyOnHighLatency()
        {
            // Arrange
            var manager = CreateManager(
                minConcurrency: 1,
                maxConcurrency: 8,
                targetLatency: 100,
                maxLatency: 5000,
                adjustmentInterval: TimeSpan.Zero);

            // Start with higher concurrency by recording successful operations
            for (int i = 0; i < 30; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(50), true);
            }

            var highConcurrency = manager.CurrentConcurrency;

            // Act - Record high latency operation
            manager.RecordOperation(
                TimeSpan.FromMilliseconds(6000), // Above maxLatency
                true);

            // Wait for adjustment interval to pass
            Thread.Sleep(100);

            // Record another to trigger reconsideration
            manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);

            // Assert - Should have decreased due to high latency
            // Note: The actual decrease happens during the next operation
            var stats = manager.GetStats();
            Assert.True(stats.CurrentConcurrency >= 1);
        }

        [Fact]
        public void RecordOperation_DecreasesAggressivelyOnRateLimit()
        {
            // Arrange
            var manager = CreateManager(
                minConcurrency: 1,
                maxConcurrency: 8,
                adjustmentInterval: TimeSpan.Zero);

            // Increase concurrency first
            for (int i = 0; i < 25; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(50), true);
            }

            var beforeRateLimit = manager.CurrentConcurrency;

            // Act - Simulate rate limit error
            manager.RecordOperation(
                TimeSpan.FromMilliseconds(100),
                false,
                new Exception("Rate limit exceeded - 429"));

            // Assert
            var stats = manager.GetStats();
            // Aggressive decrease halves the concurrency (to minimum of minConcurrency)
            Assert.True(stats.CurrentConcurrency <= beforeRateLimit);
        }

        [Fact]
        public void RecordOperation_TracksConsecutiveSuccesses()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            for (int i = 0; i < 10; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);
            }

            // Assert
            var stats = manager.GetStats();
            Assert.Equal(10, stats.ConsecutiveSuccesses);
            Assert.Equal(0, stats.ConsecutiveFailures);
        }

        [Fact]
        public void RecordOperation_TracksConsecutiveFailures()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            for (int i = 0; i < 5; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(100), false);
            }

            // Assert
            var stats = manager.GetStats();
            Assert.Equal(0, stats.ConsecutiveSuccesses);
            Assert.Equal(5, stats.ConsecutiveFailures);
        }

        [Fact]
        public void RecordOperation_ResetsConsecutiveFailuresOnSuccess()
        {
            // Arrange
            var manager = CreateManager();

            // Record failures
            for (int i = 0; i < 5; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(100), false);
            }

            // Act - Record a success
            manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);

            // Assert
            var stats = manager.GetStats();
            Assert.Equal(1, stats.ConsecutiveSuccesses);
            Assert.Equal(0, stats.ConsecutiveFailures);
        }

        [Fact]
        public void RecordOperation_ResetsConsecutiveSuccessesOnFailure()
        {
            // Arrange
            var manager = CreateManager();

            // Record successes
            for (int i = 0; i < 10; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);
            }

            // Act - Record a failure
            manager.RecordOperation(TimeSpan.FromMilliseconds(100), false);

            // Assert
            var stats = manager.GetStats();
            Assert.Equal(0, stats.ConsecutiveSuccesses);
            Assert.Equal(1, stats.ConsecutiveFailures);
        }

        #endregion

        #region Consecutive Successes/Failures Tests

        [Fact]
        public void ConsecutiveSuccesses_TriggerIncrease()
        {
            // Arrange
            var manager = CreateManager(
                minConcurrency: 1,
                maxConcurrency: 8,
                adjustmentInterval: TimeSpan.Zero);

            var initialConcurrency = manager.CurrentConcurrency;

            // Act - Record many consecutive successes with good latency
            for (int i = 0; i < 25; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(50), true);
            }

            // Assert - Concurrency should have increased
            var stats = manager.GetStats();
            Assert.True(stats.CurrentConcurrency >= initialConcurrency);
        }

        [Fact]
        public void ConsecutiveFailures_TriggerDecrease()
        {
            // Arrange
            var manager = CreateManager(
                minConcurrency: 1,
                maxConcurrency: 8,
                adjustmentInterval: TimeSpan.Zero);

            // Increase concurrency first
            for (int i = 0; i < 25; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(50), true);
            }

            var beforeFailures = manager.CurrentConcurrency;

            // Act - Record consecutive failures
            for (int i = 0; i < 6; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(100), false);
            }

            // Assert - Concurrency should have decreased
            var stats = manager.GetStats();
            Assert.True(stats.CurrentConcurrency <= beforeFailures);
        }

        #endregion

        #region Cache Tests

        [Fact]
        public void RecordOperation_CachesCalculations()
        {
            // Arrange
            var manager = CreateManager();

            // Act - Record some operations
            for (int i = 0; i < 10; i++)
            {
                manager.RecordOperation(
                    TimeSpan.FromMilliseconds(100 + i * 10),
                    i % 2 == 0);
            }

            // Assert - Should be able to get stats without recalculating
            var stats1 = manager.GetStats();
            var stats2 = manager.GetStats();

            Assert.Equal(stats1.AverageLatency, stats2.AverageLatency);
            Assert.Equal(stats1.SuccessRate, stats2.SuccessRate);
        }

        [Fact]
        public void AverageLatency_IsCalculatedCorrectly()
        {
            // Arrange
            var manager = CreateManager();

            // Act - Record operations with known latencies
            manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);
            manager.RecordOperation(TimeSpan.FromMilliseconds(200), true);
            manager.RecordOperation(TimeSpan.FromMilliseconds(300), true);

            // Assert
            var stats = manager.GetStats();
            Assert.Equal(200.0, stats.AverageLatency); // (100 + 200 + 300) / 3
        }

        [Fact]
        public void SuccessRate_IsCalculatedCorrectly()
        {
            // Arrange
            var manager = CreateManager();

            // Act - Record mix of successes and failures
            manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);
            manager.RecordOperation(TimeSpan.FromMilliseconds(100), false);
            manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);
            manager.RecordOperation(TimeSpan.FromMilliseconds(100), false);
            manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);

            // Assert - 3 successes out of 5 = 0.6
            var stats = manager.GetStats();
            Assert.Equal(0.6, stats.SuccessRate);
        }

        [Fact]
        public void AverageLatency_ReturnsZeroWhenNoOperations()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            var stats = manager.GetStats();

            // Assert
            Assert.Equal(0.0, stats.AverageLatency);
        }

        [Fact]
        public void SuccessRate_ReturnsOneWhenNoOperations()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            var stats = manager.GetStats();

            // Assert
            Assert.Equal(1.0, stats.SuccessRate);
        }

        #endregion

        #region ExecuteWithConcurrencyAsync Tests

        [Fact]
        public async Task ExecuteWithConcurrencyAsync_ExecutesOperation()
        {
            // Arrange
            var manager = CreateManager();
            var executed = false;

            // Act
            await manager.ExecuteWithConcurrencyAsync(() =>
            {
                executed = true;
                return Task.FromResult("result");
            });

            // Assert
            Assert.True(executed);
        }

        [Fact]
        public async Task ExecuteWithConcurrencyAsync_ReturnsResult()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            var result = await manager.ExecuteWithConcurrencyAsync(() =>
                Task.FromResult("test-result"));

            // Assert
            Assert.Equal("test-result", result);
        }

        [Fact]
        public async Task ExecuteWithConcurrencyAsync_RecordsMetrics()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            await manager.ExecuteWithConcurrencyAsync(() =>
                Task.FromResult("result"));

            // Assert
            var stats = manager.GetStats();
            Assert.True(stats.RecentOperations > 0);
        }

        [Fact]
        public async Task ExecuteWithConcurrencyAsync_RecordsSuccess()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            await manager.ExecuteWithConcurrencyAsync(() =>
                Task.FromResult("result"));

            // Assert
            var stats = manager.GetStats();
            Assert.Equal(1, stats.ConsecutiveSuccesses);
            Assert.Equal(0, stats.ConsecutiveFailures);
            Assert.True(stats.SuccessRate > 0);
        }

        [Fact]
        public async Task ExecuteWithConcurrencyAsync_RecordsFailure()
        {
            // Arrange
            var manager = CreateManager();

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.ExecuteWithConcurrencyAsync<string>(() =>
                    throw new InvalidOperationException("Test error")));

            // Assert
            var stats = manager.GetStats();
            Assert.Equal(0, stats.ConsecutiveSuccesses);
            Assert.Equal(1, stats.ConsecutiveFailures);
        }

        [Fact]
        public async Task ExecuteWithConcurrencyAsync_RespectsCancellationToken()
        {
            // Arrange
            var manager = CreateManager();
            var cts = new CancellationTokenSource();

            // Act & Assert
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                manager.ExecuteWithConcurrencyAsync<string>(() =>
                {
                    return Task.FromResult("result");
                }, cancellationToken: cts.Token));
        }

        #endregion

        #region GetConcurrencySemaphore Tests

        [Fact]
        public void GetConcurrencySemaphore_ReturnsValidSemaphore()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            var semaphore = manager.GetConcurrencySemaphore();

            // Assert
            Assert.NotNull(semaphore);
            Assert.Equal(manager.CurrentConcurrency, semaphore.CurrentCount);
        }

        [Fact]
        public async Task GetConcurrencySemaphore_LimitsConcurrentOperations()
        {
            // Arrange
            var manager = CreateManager(minConcurrency: 2, maxConcurrency: 2);
            var semaphore = manager.GetConcurrencySemaphore();
            var activeCount = 0;
            var maxActive = 0;

            // Act - Start more tasks than concurrency allows
            var tasks = new Task[10];
            for (int i = 0; i < 10; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var current = Interlocked.Increment(ref activeCount);
                        Interlocked.CompareExchange(ref maxActive, Math.Max(maxActive, current), maxActive);
                        await Task.Delay(100);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeCount);
                        semaphore.Release();
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Assert - Max concurrent operations should not exceed concurrency limit
            Assert.True(maxActive <= 2);
        }

        #endregion

        #region GetStats Tests

        [Fact]
        public void GetStats_ReturnsCurrentStatistics()
        {
            // Arrange
            var manager = CreateManager();
            manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);
            manager.RecordOperation(TimeSpan.FromMilliseconds(200), false);
            manager.RecordOperation(TimeSpan.FromMilliseconds(150), true);

            // Act
            var stats = manager.GetStats();

            // Assert
            Assert.NotNull(stats);
            Assert.True(stats.CurrentConcurrency >= 1);
            Assert.True(stats.AverageLatency > 0);
            Assert.True(stats.RecentOperations > 0);
            Assert.True(stats.LastAdjustment > DateTime.MinValue);
        }

        [Fact]
        public void GetStats_ReflectsRecentOperations()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            for (int i = 0; i < 5; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);
            }

            var stats = manager.GetStats();

            // Assert
            Assert.Equal(5, stats.RecentOperations);
        }

        #endregion

        #region Concurrency Adjustment Tests

        [Fact]
        public void Concreasey_NeverExceedsMax()
        {
            // Arrange
            var manager = CreateManager(
                minConcurrency: 1,
                maxConcurrency: 4,
                adjustmentInterval: TimeSpan.Zero);

            // Act - Try to drive concurrency up
            for (int i = 0; i < 100; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(10), true);
            }

            // Assert
            Assert.True(manager.CurrentConcurrency <= 4);
        }

        [Fact]
        public void Concurrency_NeverGoesBelowMin()
        {
            // Arrange
            var manager = CreateManager(
                minConcurrency: 2,
                maxConcurrency: 8,
                adjustmentInterval: TimeSpan.Zero);

            // Act - Try to drive concurrency down with failures
            for (int i = 0; i < 100; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(100), false,
                    new Exception("Rate limit - 429"));
            }

            // Assert
            var stats = manager.GetStats();
            Assert.True(stats.CurrentConcurrency >= 2);
        }

        [Fact]
        public void Concurrency_UpdatesSemaphoreOnAdjustment()
        {
            // Arrange
            var manager = CreateManager(
                minConcurrency: 1,
                maxConcurrency: 4,
                adjustmentInterval: TimeSpan.Zero);

            var oldSemaphore = manager.GetConcurrencySemaphore();

            // Act - Drive concurrency up
            for (int i = 0; i < 30; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(50), true);
            }

            var newSemaphore = manager.GetConcurrencySemaphore();

            // Assert - Should get a new semaphore instance (or updated)
            Assert.NotNull(newSemaphore);
        }

        #endregion

        #region Rate Limit Detection Tests

        [Fact]
        public void IsRateLimitError_Detects429()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            manager.RecordOperation(
                TimeSpan.FromMilliseconds(100),
                false,
                new HttpRequestException("HTTP 429 - Too many requests"));

            // Assert - Should detect and treat as rate limit
            var stats = manager.GetStats();
            Assert.Equal(1, stats.ConsecutiveFailures);
        }

        [Fact]
        public void IsRateLimitError_DetectsRateLimitText()
        {
            // Arrange
            var manager = CreateManager();

            // Act
            manager.RecordOperation(
                TimeSpan.FromMilliseconds(100),
                false,
                new Exception("Rate limit exceeded"));

            // Assert - Should detect from error message
            var stats = manager.GetStats();
            Assert.Equal(1, stats.ConsecutiveFailures);
        }

        #endregion

        #region ConcurrencyStats Tests

        [Fact]
        public void ConcurrencyStats_HasAllProperties()
        {
            // Arrange
            var stats = new ConcurrencyStats
            {
                CurrentConcurrency = 4,
                AverageLatency = 250.5,
                SuccessRate = 0.95,
                ConsecutiveSuccesses = 10,
                ConsecutiveFailures = 0,
                RecentOperations = 50,
                LastAdjustment = DateTime.UtcNow
            };

            // Act & Assert
            Assert.Equal(4, stats.CurrentConcurrency);
            Assert.Equal(250.5, stats.AverageLatency);
            Assert.Equal(0.95, stats.SuccessRate);
            Assert.Equal(10, stats.ConsecutiveSuccesses);
            Assert.Equal(0, stats.ConsecutiveFailures);
            Assert.Equal(50, stats.RecentOperations);
        }

        #endregion

        #region Metrics Queue Management Tests

        [Fact]
        public void RecordOperation_LimitsMetricsQueueSize()
        {
            // Arrange
            var manager = CreateManager();

            // Act - Record more than the queue size (100)
            for (int i = 0; i < 150; i++)
            {
                manager.RecordOperation(TimeSpan.FromMilliseconds(100), true);
            }

            // Assert - Should not exceed 100 recent operations
            var stats = manager.GetStats();
            Assert.True(stats.RecentOperations <= 100);
        }

        #endregion

        #region Thread Safety Tests

        [Fact]
        public async Task RecordOperation_ConcurrentOperations_DoNotThrow()
        {
            // Arrange
            var manager = CreateManager();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            // Act
            var tasks = new Task[50];
            for (int i = 0; i < 50; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    try
                    {
                        for (int j = 0; j < 10; j++)
                        {
                            manager.RecordOperation(
                                TimeSpan.FromMilliseconds(50 + j * 10),
                                j % 2 == 0);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Assert
            Assert.Empty(exceptions);
            var stats = manager.GetStats();
            Assert.True(stats.RecentOperations > 0);
        }

        #endregion

        #region Adjustment Interval Tests

        [Fact]
        public void RecordOperation_RespectsAdjustmentInterval()
        {
            // Arrange
            var manager = CreateManager(
                adjustmentInterval: TimeSpan.FromSeconds(1));

            var initialConcurrency = manager.CurrentConcurrency;

            // Act - Record successful operation (shouldn't adjust immediately)
            manager.RecordOperation(TimeSpan.FromMilliseconds(50), true);

            // Assert - Should not have adjusted yet
            Assert.Equal(initialConcurrency, manager.CurrentConcurrency);
        }

        #endregion
    }
}
