using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Performance
{
    /// <summary>
    /// Interface for batch memory management to prevent out-of-memory issues during large operations
    /// </summary>
    public interface IBatchMemoryManager : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Processes items in memory-safe batches with adaptive sizing
        /// </summary>
        /// <typeparam name="TInput">Type of input items</typeparam>
        /// <typeparam name="TResult">Type of processed results</typeparam>
        /// <param name="items">Items to process</param>
        /// <param name="processor">Function to process each batch</param>
        /// <param name="options">Memory management options</param>
        /// <param name="progress">Progress reporter</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Streaming results with memory management</returns>
        IAsyncEnumerable<StreamingBatchResult<TResult>> ProcessWithMemoryManagementAsync<TInput, TResult>(
            IEnumerable<TInput> items,
            Func<IEnumerable<TInput>, CancellationToken, Task<IEnumerable<TResult>>> processor,
            BatchMemoryOptions options = null,
            IProgress<BatchMemoryProgress> progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets current memory statistics for monitoring
        /// </summary>
        /// <returns>Current memory usage and processing statistics</returns>
        BatchMemoryStatistics GetMemoryStatistics();

        /// <summary>
        /// Gets optimal batch size for current memory conditions
        /// </summary>
        /// <typeparam name="T">Type being processed (affects memory calculation)</typeparam>
        /// <returns>Recommended batch size</returns>
        int GetOptimalBatchSize<T>();

        /// <summary>
        /// Checks if system should pause for memory recovery
        /// </summary>
        /// <returns>True if high memory pressure detected</returns>
        bool ShouldPauseForMemory();
    }

    /// <summary>
    /// Universal batch memory manager for preventing OOM during large streaming operations
    /// Provides adaptive batch sizing, memory monitoring, and streaming processing
    /// </summary>
    /// <remarks>
    /// Critical for streaming plugins processing large discographies:
    /// - Prevents OutOfMemoryException on 10,000+ album searches
    /// - Adapts batch sizes based on available system memory  
    /// - Provides streaming results for immediate processing
    /// - Monitors memory pressure and applies throttling
    /// - Supports graceful degradation under memory constraints
    /// 
    /// Used by Qobuzarr for processing massive classical discographies safely
    /// Essential for any plugin dealing with large batch operations
    /// </remarks>
    public class BatchMemoryManager : IBatchMemoryManager
    {
        private readonly Timer _memoryMonitorTimer;
        private readonly object _memoryLock = new();
        private readonly SemaphoreSlim _disposeSemaphore = new(1, 1);
        private bool _disposed = false;

        // Memory thresholds and limits
        private readonly long _maxMemoryBytes;
        private readonly long _criticalThresholdBytes;
        private volatile bool _isMemoryPressureHigh = false;
        private volatile int _currentOptimalBatchSize;
        private DateTime _lastGCTime = DateTime.MinValue;
        private readonly TimeSpan _gcInterval = TimeSpan.FromMinutes(2);

        // Statistics
        private long _totalItemsProcessed = 0;
        private long _totalBatchesExecuted = 0;
        private readonly DateTime _startTime = DateTime.UtcNow;

        // Configuration constants
        private const long DEFAULT_MAX_MEMORY_MB = 512;
        private const long CRITICAL_MEMORY_THRESHOLD_MB = 100;
        private const int DEFAULT_MIN_BATCH_SIZE = 10;
        private const int DEFAULT_MAX_BATCH_SIZE = 1000;
        private const int DEFAULT_INITIAL_BATCH_SIZE = 100;

        public BatchMemoryManager(long maxMemoryMB = DEFAULT_MAX_MEMORY_MB)
        {
            _maxMemoryBytes = maxMemoryMB * 1024 * 1024;
            _criticalThresholdBytes = CRITICAL_MEMORY_THRESHOLD_MB * 1024 * 1024;
            _currentOptimalBatchSize = DEFAULT_INITIAL_BATCH_SIZE;

            // Start memory monitoring
            _memoryMonitorTimer = new Timer(MonitorMemoryUsage, null,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        public async IAsyncEnumerable<StreamingBatchResult<TResult>> ProcessWithMemoryManagementAsync<TInput, TResult>(
            IEnumerable<TInput> items,
            Func<IEnumerable<TInput>, CancellationToken, Task<IEnumerable<TResult>>> processor,
            BatchMemoryOptions options = null,
            IProgress<BatchMemoryProgress> progress = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BatchMemoryManager));

            options ??= BatchMemoryOptions.Default;
            var totalItems = items is ICollection<TInput> collection ? collection.Count : items.Count();
            var processedItems = 0;
            var batchNumber = 0;

            using var enumerator = items.GetEnumerator();
            var hasItems = true;

            try
            {
                while (hasItems && !cancellationToken.IsCancellationRequested && !_disposed)
                {
                    // Wait for memory pressure to subside if needed
                    await WaitForMemoryAvailabilityAsync(cancellationToken);

                    // Determine optimal batch size based on current memory conditions
                    var batchSize = DetermineOptimalBatchSize(options, totalItems - processedItems);

                    // Extract next batch
                    var batch = new List<TInput>();
                    var itemsInBatch = 0;

                    while (itemsInBatch < batchSize && hasItems)
                    {
                        if (enumerator.MoveNext())
                        {
                            batch.Add(enumerator.Current);
                            itemsInBatch++;
                        }
                        else
                        {
                            hasItems = false;
                        }
                    }

                    if (batch.Count == 0)
                        break;

                    batchNumber++;
                    var batchStartTime = DateTime.UtcNow;

                    StreamingBatchResult<TResult>? streamingResult = null;
                    try
                    {
                        // Process the batch
                        var batchResults = await processor(batch, cancellationToken);
                        var resultsList = batchResults?.ToList() ?? new List<TResult>();

                        // Update statistics
                        processedItems += batch.Count;
                        _totalItemsProcessed += batch.Count;
                        _totalBatchesExecuted++;

                        var batchDuration = DateTime.UtcNow - batchStartTime;

                        // Adapt batch size based on performance
                        AdaptBatchSizeBasedOnPerformance(batchSize, batchDuration, batch.Count, resultsList.Count);

                        // Report progress
                        progress?.Report(new BatchMemoryProgress
                        {
                            ProcessedItems = processedItems,
                            TotalItems = totalItems,
                            CurrentBatchSize = batch.Count,
                            OptimalBatchSize = _currentOptimalBatchSize,
                            BatchNumber = batchNumber,
                            MemoryUsageMB = GetCurrentMemoryUsageMB(),
                            IsMemoryPressureHigh = _isMemoryPressureHigh,
                            BatchDuration = batchDuration
                        });

                        // Create streaming result
                        streamingResult = new StreamingBatchResult<TResult>
                        {
                            Results = resultsList,
                            BatchNumber = batchNumber,
                            ItemsInBatch = batch.Count,
                            ProcessedItems = processedItems,
                            TotalItems = totalItems,
                            IsCompleted = processedItems >= totalItems,
                            BatchDuration = batchDuration,
                            MemoryUsageMB = GetCurrentMemoryUsageMB()
                        };

                        // Perform cleanup if needed
                        if (options.EnablePeriodicCleanup && ShouldPerformCleanup())
                        {
                            await PerformMemoryCleanupAsync();
                        }
                    }
                    catch (OutOfMemoryException)
                    {
                        // Emergency memory management
                        await HandleOutOfMemoryAsync();

                        // Retry with smaller batch if possible
                        if (batch.Count > DEFAULT_MIN_BATCH_SIZE)
                        {
                            var smallerBatch = batch.Take(Math.Max(1, batch.Count / 2)).ToList();

                            try
                            {
                                var retryResults = await processor(smallerBatch, cancellationToken);
                                var retryResultsList = retryResults?.ToList() ?? new List<TResult>();

                                processedItems += smallerBatch.Count;

                                streamingResult = new StreamingBatchResult<TResult>
                                {
                                    Results = retryResultsList,
                                    BatchNumber = batchNumber,
                                    ItemsInBatch = smallerBatch.Count,
                                    ProcessedItems = processedItems,
                                    TotalItems = totalItems,
                                    IsCompleted = false,
                                    BatchDuration = DateTime.UtcNow - batchStartTime,
                                    MemoryUsageMB = GetCurrentMemoryUsageMB(),
                                    HasMemoryIssue = true
                                };
                            }
                            catch (Exception)
                            {
                                throw;
                            }
                        }
                        else
                        {
                            throw;
                        }
                    }
                    catch (Exception ex)
                    {
                        streamingResult = new StreamingBatchResult<TResult>
                        {
                            Results = new List<TResult>(),
                            BatchNumber = batchNumber,
                            ItemsInBatch = batch.Count,
                            ProcessedItems = processedItems,
                            TotalItems = totalItems,
                            IsCompleted = false,
                            BatchDuration = DateTime.UtcNow - batchStartTime,
                            MemoryUsageMB = GetCurrentMemoryUsageMB(),
                            ErrorMessage = ex.Message,
                            HasError = true
                        };

                        if (!options.ContinueOnError)
                            throw;
                    }

                    // Yield the result after all try-catch handling
                    if (streamingResult != null)
                    {
                        yield return streamingResult;
                    }
                }
            }
            finally
            {
                // Ensure cleanup happens even if enumeration is aborted
                if (!_disposed)
                {
                    await PerformMemoryCleanupAsync();
                }
            }
        }

        public BatchMemoryStatistics GetMemoryStatistics()
        {
            var currentMemory = GetCurrentMemoryUsageMB();
            var availableMemory = Math.Max(0, (_maxMemoryBytes / 1024 / 1024) - currentMemory);

            return new BatchMemoryStatistics
            {
                CurrentMemoryUsageMB = currentMemory,
                MaxMemoryLimitMB = _maxMemoryBytes / 1024 / 1024,
                AvailableMemoryMB = availableMemory,
                IsMemoryPressureHigh = _isMemoryPressureHigh,
                OptimalBatchSize = _currentOptimalBatchSize,
                TotalItemsProcessed = _totalItemsProcessed,
                TotalBatchesExecuted = _totalBatchesExecuted,
                ProcessingDuration = DateTime.UtcNow - _startTime,
                AverageBatchSize = _totalBatchesExecuted > 0 ? _totalItemsProcessed / _totalBatchesExecuted : 0
            };
        }

        public int GetOptimalBatchSize<T>()
        {
            return _currentOptimalBatchSize;
        }

        public bool ShouldPauseForMemory()
        {
            return _isMemoryPressureHigh;
        }

        #region Private Implementation

        private void MonitorMemoryUsage(object? state)
        {
            if (_disposed)
                return;

            try
            {
                lock (_memoryLock)
                {
                    var currentMemoryMB = GetCurrentMemoryUsageMB();
                    var availableMemory = (_maxMemoryBytes / 1024 / 1024) - currentMemoryMB;

                    _isMemoryPressureHigh = availableMemory < (_criticalThresholdBytes / 1024 / 1024);
                }
            }
            catch
            {
                // Ignore monitoring errors
            }
        }

        private async Task WaitForMemoryAvailabilityAsync(CancellationToken cancellationToken)
        {
            var waitStartTime = DateTime.UtcNow;
            var maxWaitTime = TimeSpan.FromMinutes(5);

            while (_isMemoryPressureHigh && DateTime.UtcNow - waitStartTime < maxWaitTime)
            {
                GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
                await Task.Delay(5000, cancellationToken);
            }
        }

        private int DetermineOptimalBatchSize(BatchMemoryOptions options, int remainingItems)
        {
            var baseSize = _currentOptimalBatchSize;

            // Adjust based on memory pressure
            if (_isMemoryPressureHigh)
            {
                baseSize = Math.Max(options.MinBatchSize, baseSize / 2);
            }
            else
            {
                // Can potentially increase if memory is abundant
                var currentMemoryMB = GetCurrentMemoryUsageMB();
                var memoryUtilization = (double)currentMemoryMB / (_maxMemoryBytes / 1024 / 1024);

                if (memoryUtilization < 0.5) // Less than 50% memory used
                {
                    baseSize = Math.Min(options.MaxBatchSize, (int)(baseSize * 1.2));
                }
            }

            // Don't exceed remaining items
            return Math.Min(baseSize, remainingItems);
        }

        private void AdaptBatchSizeBasedOnPerformance(int batchSize, TimeSpan duration, int inputCount, int outputCount)
        {
            // Calculate items processed per second
            var itemsPerSecond = duration.TotalSeconds > 0 ? inputCount / duration.TotalSeconds : 0;

            // Adjust batch size based on throughput
            if (itemsPerSecond > 50) // High throughput - can handle larger batches
            {
                _currentOptimalBatchSize = Math.Min(DEFAULT_MAX_BATCH_SIZE,
                    (int)(_currentOptimalBatchSize * 1.1));
            }
            else if (itemsPerSecond < 10) // Low throughput - reduce batch size
            {
                _currentOptimalBatchSize = Math.Max(DEFAULT_MIN_BATCH_SIZE,
                    (int)(_currentOptimalBatchSize * 0.9));
            }
        }

        private long GetCurrentMemoryUsageMB()
        {
            using var process = Process.GetCurrentProcess();
            return process.WorkingSet64 / 1024 / 1024;
        }

        private bool ShouldPerformCleanup()
        {
            return DateTime.UtcNow - _lastGCTime > _gcInterval || _isMemoryPressureHigh;
        }

        private async Task PerformMemoryCleanupAsync()
        {
            var beforeMemory = GetCurrentMemoryUsageMB();

            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
            await Task.Delay(100);

            _lastGCTime = DateTime.UtcNow;
        }

        private async Task HandleOutOfMemoryAsync()
        {
            // Emergency batch size reduction
            _currentOptimalBatchSize = Math.Max(1, DEFAULT_MIN_BATCH_SIZE / 2);

            // Aggressive cleanup
            await PerformMemoryCleanupAsync();

            // Give system time to recover
            await Task.Delay(2000);
        }

        #endregion

        #region Disposal

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore();
            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (_disposed)
                return;

            await _disposeSemaphore.WaitAsync();
            try
            {
                if (!_disposed)
                {
                    GC.Collect(0, GCCollectionMode.Optimized, blocking: false);

                    if (_memoryMonitorTimer != null)
                    {
                        await _memoryMonitorTimer.DisposeAsync();
                    }

                    _disposed = true;
                }
            }
            finally
            {
                _disposeSemaphore.Release();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposeSemaphore.Wait();
            try
            {
                if (!_disposed)
                {
                    if (disposing)
                    {
                        _memoryMonitorTimer?.Dispose();
                        _disposeSemaphore?.Dispose();
                    }

                    _disposed = true;
                }
            }
            finally
            {
                _disposeSemaphore?.Release();
            }
        }

        #endregion
    }

    /// <summary>
    /// Options for batch memory management configuration
    /// </summary>
    public class BatchMemoryOptions
    {
        public int MinBatchSize { get; set; } = 10;
        public int MaxBatchSize { get; set; } = 100;
        public bool EnablePeriodicCleanup { get; set; } = true;
        public bool ContinueOnError { get; set; } = true;
        public TimeSpan MaxWaitTime { get; set; } = TimeSpan.FromMinutes(5);

        public static BatchMemoryOptions Default => new();

        public static BatchMemoryOptions Conservative => new()
        {
            MinBatchSize = 5,
            MaxBatchSize = 50,
            EnablePeriodicCleanup = true,
            ContinueOnError = false
        };

        public static BatchMemoryOptions Aggressive => new()
        {
            MinBatchSize = 50,
            MaxBatchSize = 2000,
            EnablePeriodicCleanup = false,
            ContinueOnError = true
        };
    }

    /// <summary>
    /// Progress information for batch memory operations
    /// </summary>
    public class BatchMemoryProgress
    {
        public int ProcessedItems { get; set; }
        public int TotalItems { get; set; }
        public int CurrentBatchSize { get; set; }
        public int OptimalBatchSize { get; set; }
        public int BatchNumber { get; set; }
        public long MemoryUsageMB { get; set; }
        public bool IsMemoryPressureHigh { get; set; }
        public TimeSpan BatchDuration { get; set; }

        public double PercentComplete => TotalItems > 0 ? (double)ProcessedItems / TotalItems * 100 : 0;
        public double ItemsPerSecond => BatchDuration.TotalSeconds > 0 ? CurrentBatchSize / BatchDuration.TotalSeconds : 0;
    }

    /// <summary>
    /// Result of a streaming batch operation
    /// </summary>
    public class StreamingBatchResult<TResult>
    {
        public List<TResult> Results { get; set; } = new();
        public int BatchNumber { get; set; }
        public int ItemsInBatch { get; set; }
        public int ProcessedItems { get; set; }
        public int TotalItems { get; set; }
        public bool IsCompleted { get; set; }
        public TimeSpan BatchDuration { get; set; }
        public long MemoryUsageMB { get; set; }
        public bool HasError { get; set; }
        public bool HasMemoryIssue { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;

        public double PercentComplete => TotalItems > 0 ? (double)ProcessedItems / TotalItems * 100 : 0;
    }

    /// <summary>
    /// Memory statistics for monitoring and diagnostics
    /// </summary>
    public class BatchMemoryStatistics
    {
        public long CurrentMemoryUsageMB { get; set; }
        public long MaxMemoryLimitMB { get; set; }
        public long AvailableMemoryMB { get; set; }
        public bool IsMemoryPressureHigh { get; set; }
        public int OptimalBatchSize { get; set; }
        public long TotalItemsProcessed { get; set; }
        public long TotalBatchesExecuted { get; set; }
        public TimeSpan ProcessingDuration { get; set; }
        public long AverageBatchSize { get; set; }

        public double MemoryUtilizationPercent => MaxMemoryLimitMB > 0 ?
            (double)CurrentMemoryUsageMB / MaxMemoryLimitMB * 100 : 0;
        public double AverageItemsPerSecond => ProcessingDuration.TotalSeconds > 0 ?
            TotalItemsProcessed / ProcessingDuration.TotalSeconds : 0;
    }
}