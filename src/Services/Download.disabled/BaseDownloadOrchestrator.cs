using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Performance;
using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// Interface for download orchestration across streaming services
    /// </summary>
    public interface IDownloadOrchestrator<TTrack, TAlbum, TSettings> : IDisposable
        where TTrack : class
        where TAlbum : class  
        where TSettings : class
    {
        /// <summary>
        /// Downloads a complete album with progress tracking and error handling
        /// </summary>
        Task<AlbumDownloadResult> DownloadAlbumAsync(TAlbum album, TSettings settings, 
            string outputDirectory, IProgress<DownloadProgress> progress = null, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads a single track
        /// </summary>
        Task<TrackDownloadResult> DownloadTrackAsync(TTrack track, TSettings settings,
            string outputPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads multiple tracks with batch processing
        /// </summary>
        IAsyncEnumerable<TrackDownloadResult> DownloadTracksAsync(IEnumerable<TTrack> tracks,
            TSettings settings, string outputDirectory, IProgress<DownloadProgress> progress = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Base class for download orchestration providing common patterns for streaming services
    /// Integrates rate limiting, memory management, progress tracking, and error handling
    /// </summary>
    /// <typeparam name="TTrack">Service-specific track model</typeparam>
    /// <typeparam name="TAlbum">Service-specific album model</typeparam>
    /// <typeparam name="TSettings">Service-specific settings</typeparam>
    /// <remarks>
    /// Provides unified download orchestration patterns extracted from:
    /// - Qobuzarr: Advanced batch processing with memory management
    /// - Tidalarr: Streaming download with chunk processing
    /// - TrevTV's: Simple, reliable download patterns
    /// 
    /// Key Features:
    /// - Memory-safe batch processing for large albums
    /// - Adaptive rate limiting per streaming service
    /// - Progress tracking with detailed statistics
    /// - Automatic retry with exponential backoff
    /// - File path sanitization and collision handling
    /// - Quality fallback and validation
    /// - Concurrent download limiting
    /// </remarks>
    public abstract class BaseDownloadOrchestrator<TTrack, TAlbum, TSettings> 
        : IDownloadOrchestrator<TTrack, TAlbum, TSettings>
        where TTrack : class
        where TAlbum : class
        where TSettings : class
    {
        protected readonly IUniversalAdaptiveRateLimiter _rateLimiter;
        protected readonly IBatchMemoryManager _memoryManager;
        protected readonly SemaphoreSlim _concurrencyLimiter;
        protected readonly string _serviceName;
        private bool _disposed;

        // Configuration
        private const int DEFAULT_MAX_CONCURRENT_DOWNLOADS = 3;
        private const int DEFAULT_RETRY_ATTEMPTS = 3;

        protected BaseDownloadOrchestrator(
            string serviceName,
            IUniversalAdaptiveRateLimiter rateLimiter = null,
            IBatchMemoryManager memoryManager = null,
            int maxConcurrentDownloads = DEFAULT_MAX_CONCURRENT_DOWNLOADS)
        {
            _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
            _rateLimiter = rateLimiter ?? new UniversalAdaptiveRateLimiter();
            _memoryManager = memoryManager ?? new BatchMemoryManager();
            _concurrencyLimiter = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);
        }

        public virtual async Task<AlbumDownloadResult> DownloadAlbumAsync(
            TAlbum album,
            TSettings settings, 
            string outputDirectory,
            IProgress<DownloadProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);

            var albumTracks = await GetAlbumTracksAsync(album, settings);
            var totalTracks = albumTracks.Count;
            var downloadedTracks = new List<TrackDownloadResult>();
            var errors = new List<string>();
            
            var albumStartTime = DateTime.UtcNow;
            var processedTracks = 0;

            // Use batch memory management for large albums
            var batchOptions = new BatchMemoryOptions
            {
                MinBatchSize = 1,
                MaxBatchSize = Math.Min(10, totalTracks), // Don't overwhelm streaming service
                EnablePeriodicCleanup = true,
                ContinueOnError = true
            };

            await foreach (var batchResult in _memoryManager.ProcessWithMemoryManagementAsync(
                albumTracks,
                async (trackBatch, ct) =>
                {
                    var batchResults = new List<TrackDownloadResult>();
                    
                    // Process tracks in batch with concurrency limiting
                    var downloadTasks = trackBatch.Select(async track =>
                    {
                        await _concurrencyLimiter.WaitAsync(ct);
                        try
                        {
                            var trackFileName = GenerateTrackFileName(track, album, settings);
                            var trackPath = Path.Combine(outputDirectory, trackFileName);
                            
                            return await DownloadTrackWithRetryAsync(track, settings, trackPath, ct);
                        }
                        finally
                        {
                            _concurrencyLimiter.Release();
                        }
                    });

                    var results = await Task.WhenAll(downloadTasks);
                    return results;
                },
                batchOptions,
                cancellationToken: cancellationToken))
            {
                downloadedTracks.AddRange(batchResult.Results);
                processedTracks += batchResult.ItemsInBatch;

                // Collect errors from failed downloads
                foreach (var result in batchResult.Results.Where(r => !r.Success))
                {
                    errors.Add($"Track {result.TrackTitle}: {result.ErrorMessage}");
                }

                // Report progress
                progress?.Report(new DownloadProgress
                {
                    ProcessedTracks = processedTracks,
                    TotalTracks = totalTracks,
                    SuccessfulDownloads = downloadedTracks.Count(r => r.Success),
                    FailedDownloads = downloadedTracks.Count(r => !r.Success),
                    CurrentBatch = batchResult.BatchNumber,
                    MemoryUsageMB = batchResult.MemoryUsageMB,
                    ElapsedTime = DateTime.UtcNow - albumStartTime
                });

                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            var albumDuration = DateTime.UtcNow - albumStartTime;
            var successCount = downloadedTracks.Count(r => r.Success);
            
            return new AlbumDownloadResult
            {
                AlbumTitle = GetAlbumTitle(album),
                TotalTracks = totalTracks,
                SuccessfulDownloads = successCount,
                FailedDownloads = totalTracks - successCount,
                TrackResults = downloadedTracks,
                Errors = errors,
                Duration = albumDuration,
                Success = successCount > 0,
                OutputDirectory = outputDirectory
            };
        }

        public virtual async Task<TrackDownloadResult> DownloadTrackAsync(
            TTrack track,
            TSettings settings,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            return await DownloadTrackWithRetryAsync(track, settings, outputPath, cancellationToken);
        }

        public virtual async IAsyncEnumerable<TrackDownloadResult> DownloadTracksAsync(
            IEnumerable<TTrack> tracks,
            TSettings settings,
            string outputDirectory,
            IProgress<DownloadProgress> progress = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var trackList = tracks.ToList();
            var processedCount = 0;
            var startTime = DateTime.UtcNow;

            await foreach (var batchResult in _memoryManager.ProcessWithMemoryManagementAsync(
                trackList,
                async (trackBatch, ct) =>
                {
                    var results = new List<TrackDownloadResult>();
                    
                    foreach (var track in trackBatch)
                    {
                        await _concurrencyLimiter.WaitAsync(ct);
                        try
                        {
                            var fileName = GenerateTrackFileName(track, null, settings);
                            var filePath = Path.Combine(outputDirectory, fileName);
                            
                            var result = await DownloadTrackWithRetryAsync(track, settings, filePath, ct);
                            results.Add(result);
                            
                            // Result will be yielded through batch processing
                        }
                        finally
                        {
                            _concurrencyLimiter.Release();
                        }
                    }

                    return results;
                },
                cancellationToken: cancellationToken))
            {
                processedCount += batchResult.ItemsInBatch;
                
                progress?.Report(new DownloadProgress
                {
                    ProcessedTracks = processedCount,
                    TotalTracks = trackList.Count,
                    SuccessfulDownloads = batchResult.Results.Count(r => r.Success),
                    FailedDownloads = batchResult.Results.Count(r => !r.Success),
                    CurrentBatch = batchResult.BatchNumber,
                    MemoryUsageMB = batchResult.MemoryUsageMB,
                    ElapsedTime = DateTime.UtcNow - startTime
                });
                
                // Yield each result from this batch
                foreach (var result in batchResult.Results)
                {
                    yield return result;
                }
            }
        }

        private async Task<TrackDownloadResult> DownloadTrackWithRetryAsync(
            TTrack track,
            TSettings settings,
            string outputPath,
            CancellationToken cancellationToken)
        {
            var trackTitle = GetTrackTitle(track);
            var startTime = DateTime.UtcNow;
            
            for (int attempt = 1; attempt <= DEFAULT_RETRY_ATTEMPTS; attempt++)
            {
                try
                {
                    // Wait for rate limiting
                    await _rateLimiter.WaitIfNeededAsync(_serviceName, "download", cancellationToken);

                    // Create directory if needed
                    var directory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Download track using service-specific implementation
                    var trackData = await DownloadTrackDataAsync(track, settings, cancellationToken);
                    
                    // Write to file
                    await File.WriteAllBytesAsync(outputPath, trackData, cancellationToken);
                    
                    // Apply metadata if supported
                    await ApplyMetadataAsync(track, outputPath, settings);

                    return new TrackDownloadResult
                    {
                        TrackTitle = trackTitle,
                        OutputPath = outputPath,
                        Success = true,
                        FileSize = trackData.Length,
                        Duration = DateTime.UtcNow - startTime,
                        AttemptCount = attempt
                    };
                }
                catch (Exception ex) when (attempt < DEFAULT_RETRY_ATTEMPTS)
                {
                    // Log retry attempt
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
                catch (Exception ex)
                {
                    return new TrackDownloadResult
                    {
                        TrackTitle = trackTitle,
                        OutputPath = outputPath,
                        Success = false,
                        ErrorMessage = ex.Message,
                        Duration = DateTime.UtcNow - startTime,
                        AttemptCount = attempt
                    };
                }
            }

            // Should never reach here due to exception handling above
            return new TrackDownloadResult
            {
                TrackTitle = trackTitle,
                OutputPath = outputPath,
                Success = false,
                ErrorMessage = "Unknown error occurred",
                Duration = DateTime.UtcNow - startTime,
                AttemptCount = DEFAULT_RETRY_ATTEMPTS
            };
        }

        #region Abstract Methods - Service Specific Implementation

        /// <summary>
        /// Gets tracks for an album (service-specific implementation)
        /// </summary>
        protected abstract Task<List<TTrack>> GetAlbumTracksAsync(TAlbum album, TSettings settings);

        /// <summary>
        /// Downloads track data as byte array (service-specific implementation)
        /// </summary>
        protected abstract Task<byte[]> DownloadTrackDataAsync(TTrack track, TSettings settings, CancellationToken cancellationToken);

        /// <summary>
        /// Generates appropriate file name for track (service-specific implementation)
        /// </summary>
        protected abstract string GenerateTrackFileName(TTrack track, TAlbum album = null, TSettings settings = null);

        /// <summary>
        /// Gets display title for track (service-specific implementation)
        /// </summary>
        protected abstract string GetTrackTitle(TTrack track);

        /// <summary>
        /// Gets display title for album (service-specific implementation)
        /// </summary>
        protected abstract string GetAlbumTitle(TAlbum album);

        /// <summary>
        /// Applies metadata to downloaded file (service-specific implementation, optional)
        /// </summary>
        protected virtual Task ApplyMetadataAsync(TTrack track, string filePath, TSettings settings)
        {
            // Default implementation does nothing
            return Task.CompletedTask;
        }

        #endregion

        #region Protected Helper Methods

        /// <summary>
        /// Sanitizes file name using shared utilities
        /// </summary>
        protected static string SanitizeFileName(string fileName)
        {
            return FileNameSanitizer.SanitizeFileName(fileName);
        }

        /// <summary>
        /// Generates unique file path to prevent collisions
        /// </summary>
        protected static string EnsureUniqueFilePath(string basePath)
        {
            if (!File.Exists(basePath))
                return basePath;

            var directory = Path.GetDirectoryName(basePath);
            var fileName = Path.GetFileNameWithoutExtension(basePath);
            var extension = Path.GetExtension(basePath);

            int counter = 1;
            string newPath;
            
            do
            {
                newPath = Path.Combine(directory ?? "", $"{fileName} ({counter}){extension}");
                counter++;
            } while (File.Exists(newPath) && counter < 1000); // Prevent infinite loop

            return newPath;
        }

        /// <summary>
        /// Validates output directory and creates if needed
        /// </summary>
        protected static void EnsureOutputDirectory(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        #endregion

        public virtual void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _concurrencyLimiter?.Dispose();
            _rateLimiter?.Dispose();
            _memoryManager?.Dispose();
        }
    }

    /// <summary>
    /// Result of an album download operation
    /// </summary>
    public class AlbumDownloadResult
    {
        public string AlbumTitle { get; set; } = string.Empty;
        public int TotalTracks { get; set; }
        public int SuccessfulDownloads { get; set; }
        public int FailedDownloads { get; set; }
        public List<TrackDownloadResult> TrackResults { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; }
        public string OutputDirectory { get; set; } = string.Empty;
        public long TotalBytes => TrackResults.Sum(r => r.FileSize);
        public double SuccessRate => TotalTracks > 0 ? (double)SuccessfulDownloads / TotalTracks : 0;
    }

    /// <summary>
    /// Result of a single track download operation
    /// </summary>
    public class TrackDownloadResult
    {
        public string TrackTitle { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public bool Success { get; set; }
        public long FileSize { get; set; }
        public TimeSpan Duration { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int AttemptCount { get; set; }
        public string Quality { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Progress information for download operations
    /// </summary>
    public class DownloadProgress
    {
        public int ProcessedTracks { get; set; }
        public int TotalTracks { get; set; }
        public int SuccessfulDownloads { get; set; }
        public int FailedDownloads { get; set; }
        public int CurrentBatch { get; set; }
        public long MemoryUsageMB { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public string CurrentTrack { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        
        public double PercentComplete => TotalTracks > 0 ? (double)ProcessedTracks / TotalTracks * 100 : 0;
        public double SuccessRate => ProcessedTracks > 0 ? (double)SuccessfulDownloads / ProcessedTracks : 0;
        public double TracksPerSecond => ElapsedTime.TotalSeconds > 0 ? ProcessedTracks / ElapsedTime.TotalSeconds : 0;
    }

    /// <summary>
    /// Download configuration and options
    /// </summary>
    public class DownloadOptions
    {
        public int MaxConcurrentDownloads { get; set; } = 3;
        public int MaxRetryAttempts { get; set; } = 3;
        public bool OverwriteExisting { get; set; } = false;
        public bool ApplyMetadata { get; set; } = true;
        public string PreferredQuality { get; set; } = string.Empty;
        public TimeSpan TimeoutPerTrack { get; set; } = TimeSpan.FromMinutes(5);
        public bool ContinueOnError { get; set; } = true;
        
        public static DownloadOptions Default => new();
        
        public static DownloadOptions Fast => new()
        {
            MaxConcurrentDownloads = 5,
            MaxRetryAttempts = 1,
            ApplyMetadata = false
        };
        
        public static DownloadOptions Reliable => new()
        {
            MaxConcurrentDownloads = 1,
            MaxRetryAttempts = 5,
            ContinueOnError = false
        };
    }
}