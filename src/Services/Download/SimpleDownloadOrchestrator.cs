using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Utilities;
using Lidarr.Plugin.Common.Services.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// A minimal, pluggable download orchestrator that performs robust audio downloads
    /// (partial resume + atomic move) using service-provided delegates for album/track metadata
    /// and stream URL resolution. Intended as a stepping stone to a full orchestrator.
    /// </summary>
    public class SimpleDownloadOrchestrator : IStreamingDownloadOrchestrator
    {
        private readonly HttpClient _httpClient;
        private readonly Func<string, Task<StreamingAlbum>> _getAlbumAsync;
        private readonly Func<string, Task<StreamingTrack>> _getTrackAsync;
        private readonly Func<string, Task<IReadOnlyList<string>>> _getAlbumTrackIdsAsync;
        private readonly Func<string, StreamingQuality?, Task<(string Url, string Extension)>> _getStreamAsync;
        private readonly IAudioStreamProvider? _streamProvider;
        private readonly IAudioMetadataApplier? _metadataApplier;
        private readonly ILogger _logger;
        private readonly int _maxConcurrentTracks;

        public string ServiceName { get; }

        public SimpleDownloadOrchestrator(
            string serviceName,
            HttpClient httpClient,
            Func<string, Task<StreamingAlbum>> getAlbumAsync,
            Func<string, Task<StreamingTrack>> getTrackAsync,
            Func<string, Task<IReadOnlyList<string>>> getAlbumTrackIdsAsync,
            Func<string, StreamingQuality?, Task<(string Url, string Extension)>> getStreamAsync,
            int maxConcurrentTracks = 1,
            IAudioStreamProvider? streamProvider = null,
            IAudioMetadataApplier? metadataApplier = null,
            ILogger? logger = null)
        {
            ServiceName = serviceName;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _getAlbumAsync = getAlbumAsync ?? throw new ArgumentNullException(nameof(getAlbumAsync));
            _getTrackAsync = getTrackAsync ?? throw new ArgumentNullException(nameof(getTrackAsync));
            _getAlbumTrackIdsAsync = getAlbumTrackIdsAsync ?? throw new ArgumentNullException(nameof(getAlbumTrackIdsAsync));
            _getStreamAsync = getStreamAsync ?? throw new ArgumentNullException(nameof(getStreamAsync));
            _maxConcurrentTracks = Math.Max(1, maxConcurrentTracks);
            _streamProvider = streamProvider;
            _metadataApplier = metadataApplier ?? new TagLibAudioMetadataApplier();
            _logger = logger ?? NullLogger.Instance;
        }

        public Task<DownloadResult> DownloadAlbumAsync(string albumId, string outputDirectory, StreamingQuality quality = null, IProgress<DownloadProgress> progress = null)
        {
            return DownloadAlbumAsync(albumId, outputDirectory, quality, progress, CancellationToken.None);
        }

        public virtual async Task<DownloadResult> DownloadAlbumAsync(string albumId, string outputDirectory, StreamingQuality quality, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var album = await _getAlbumAsync(albumId).ConfigureAwait(false);
            if (album == null) throw new InvalidOperationException($"Album not found: {albumId}");

            var result = new DownloadResult { Success = true, Duration = TimeSpan.Zero };
            var started = DateTime.UtcNow;
            var files = new List<string>();

            Directory.CreateDirectory(outputDirectory);
            var trackIds = await _getAlbumTrackIdsAsync(albumId).ConfigureAwait(false);
            int total = trackIds?.Count ?? album.TrackCount;

            if (trackIds == null || trackIds.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = $"No track IDs returned for album {albumId}";
                result.FilePaths = files;
                result.TotalSize = 0;
                result.Duration = DateTime.UtcNow - started;
                return result;
            }

            if (_maxConcurrentTracks <= 1)
            {
                // Sequential download path (original behavior)
                int done = 0;
                foreach (var trackId in trackIds ?? Array.Empty<string>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var track = await _getTrackAsync(trackId).ConfigureAwait(false);
                    var trackPath = Path.Combine(outputDirectory, FileSystemUtilities.CreateTrackFileName(track?.Title ?? "Unknown", track?.TrackNumber ?? 0));

                    var tr = await DownloadTrackInternalAsync(trackId, track, trackPath, quality, progress, done, total, cancellationToken).ConfigureAwait(false);
                    result.TrackResults.Add(new TrackDownloadResult
                    {
                        TrackId = trackId,
                        Success = tr.Success,
                        FilePath = tr.FilePath,
                        FileSize = tr.FileSize,
                        ActualQuality = tr.ActualQuality,
                        DownloadTime = TimeSpan.Zero,
                        ErrorMessage = tr.ErrorMessage
                    });
                    if (tr.Success) files.Add(tr.FilePath);
                    done++;
                    ReportProgress(progress, done, Math.Max(total, trackIds?.Count ?? total), track?.Title, 0, 0, null);
                }
            }
            else
            {
                // Bounded parallel download path
                using var semaphore = new SemaphoreSlim(_maxConcurrentTracks, _maxConcurrentTracks);
                var trackResultsLock = new object();
                int completed = 0;

                var tasks = (trackIds ?? Array.Empty<string>()).Select(async trackId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var track = await _getTrackAsync(trackId).ConfigureAwait(false);
                        var trackPath = Path.Combine(outputDirectory, FileSystemUtilities.CreateTrackFileName(track?.Title ?? "Unknown", track?.TrackNumber ?? 0));

                        var currentCompleted = Interlocked.CompareExchange(ref completed, 0, 0);
                        var tr = await DownloadTrackInternalAsync(trackId, track, trackPath, quality, progress, currentCompleted, total, cancellationToken).ConfigureAwait(false);

                        lock (trackResultsLock)
                        {
                            result.TrackResults.Add(new TrackDownloadResult
                            {
                                TrackId = trackId,
                                Success = tr.Success,
                                FilePath = tr.FilePath,
                                FileSize = tr.FileSize,
                                ActualQuality = tr.ActualQuality,
                                DownloadTime = TimeSpan.Zero,
                                ErrorMessage = tr.ErrorMessage
                            });
                            if (tr.Success && !string.IsNullOrEmpty(tr.FilePath))
                            {
                                files.Add(tr.FilePath);
                            }
                        }

                        var done = Interlocked.Increment(ref completed);
                        ReportProgress(progress, done, total, track?.Title, 0, 0, null);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            var failures = result.TrackResults.Where(tr => !tr.Success).ToList();
            if (failures.Count > 0)
            {
                result.Success = false;
                var first = failures[0];
                result.ErrorMessage = $"Failed to download {failures.Count}/{result.TrackResults.Count} tracks. First error: {first.ErrorMessage}";
            }
            else if (files.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "No files were downloaded.";
            }
            else if (files.Count != result.TrackResults.Count)
            {
                result.Success = false;
                result.ErrorMessage = $"Downloaded {files.Count} files but have {result.TrackResults.Count} track results.";
            }
            else
            {
                result.Success = true;
            }

            result.FilePaths = files;
            result.TotalSize = files.Where(File.Exists).Select(f => new FileInfo(f).Length).Sum();
            result.Duration = DateTime.UtcNow - started;
            return result;
        }

        public Task<TrackDownloadResult> DownloadTrackAsync(string trackId, string outputPath, StreamingQuality? quality = null)
        {
            return DownloadTrackAsync(trackId, outputPath, quality, CancellationToken.None);
        }

        public virtual async Task<TrackDownloadResult> DownloadTrackAsync(string trackId, string outputPath, StreamingQuality? quality, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = await _getTrackAsync(trackId).ConfigureAwait(false);
            if (track == null) throw new InvalidOperationException($"Track not found: {trackId}");
            return await DownloadTrackInternalAsync(trackId, track, outputPath, quality, null, 0, 1, cancellationToken).ConfigureAwait(false);
        }

        private async Task<TrackDownloadResult> DownloadTrackInternalAsync(string trackId, StreamingTrack track, string outputPath, StreamingQuality? quality, IProgress<DownloadProgress>? progress, int completedBefore, int totalTracks, CancellationToken cancellationToken)
        {
            // If a chunk-based stream provider is supplied, use it
            if (_streamProvider != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                    var tempPath = outputPath + ".partial";
                    var streamResult = await _streamProvider.GetStreamAsync(trackId, quality, cancellationToken).ConfigureAwait(false);
                    var totalExpected = streamResult.TotalBytes ?? 0;
                    if (!string.IsNullOrWhiteSpace(streamResult.SuggestedExtension))
                    {
                        outputPath = Path.ChangeExtension(outputPath, streamResult.SuggestedExtension.TrimStart('.'));
                    }

                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
                    {
                        await CopyWithProgressAsync(streamResult.Stream, fs, totalExpected, cancellationToken, (fraction, bps, eta) =>
                        {
                            ReportProgress(progress, completedBefore, totalTracks, track?.Title, fraction, bps, eta);
                        }).ConfigureAwait(false);
                    }
                    try { FileSystemUtilities.MoveFile(tempPath, outputPath, true); }
                    catch { if (File.Exists(outputPath)) File.Delete(outputPath); File.Move(tempPath, outputPath); }

                    var fileSize = new FileInfo(outputPath).Length;
                    if (fileSize <= 0)
                    {
                        TryDelete(outputPath);
                        return new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = "Downloaded file is empty" };
                    }

                    // Apply metadata tags after successful download
                    await ApplyMetadataAsync(outputPath, track, cancellationToken).ConfigureAwait(false);

                    return new TrackDownloadResult { TrackId = trackId, Success = true, FilePath = outputPath, FileSize = fileSize, ActualQuality = quality };
                }
                catch (Exception ex)
                {
                    return new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = $"Track {trackId}: {ex.Message}" };
                }
            }

            // Fallback to URL-based path with resume tracking
            return await DownloadViaUrlAsync(trackId, track, outputPath, quality, progress, completedBefore, totalTracks, cancellationToken).ConfigureAwait(false);
        }


        private async Task<TrackDownloadResult> DownloadViaUrlAsync(string trackId, StreamingTrack track, string outputPath, StreamingQuality? quality, IProgress<DownloadProgress>? progress, int completedBefore, int totalTracks, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (url, extension) = await _getStreamAsync(trackId, quality).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url)) return new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = "Empty stream URL" };
            if (!string.IsNullOrWhiteSpace(extension)) outputPath = Path.ChangeExtension(outputPath, extension.TrimStart('.'));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                cancellationToken.ThrowIfCancellationRequested();

                var tempPath = outputPath + ".partial";
                var resumePath = tempPath + ".resume.json";

                long existingBytes = 0;
                string? etag = null;
                DateTimeOffset? lastModified = null;
                if (File.Exists(tempPath))
                {
                    try { existingBytes = new FileInfo(tempPath).Length; } catch { existingBytes = 0; }
                    try
                    {
                        if (File.Exists(resumePath))
                        {
                            var json = File.ReadAllText(resumePath);
                            var state = System.Text.Json.JsonSerializer.Deserialize<ResumeState>(json);
                            if (state != null)
                            {
                                etag = state.ETag;
                                if (state.LastModifiedUtc.HasValue) lastModified = new DateTimeOffset(state.LastModifiedUtc.Value, TimeSpan.Zero);
                            }
                        }
                    }
                    catch { }
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (existingBytes > 0)
                {
                    req.Headers.Range = new RangeHeaderValue(existingBytes, null);
                    if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-Range", etag);
                    else if (lastModified.HasValue) req.Headers.TryAddWithoutValidation("If-Range", lastModified.Value.ToString("R"));
                }

                using var resp = await _httpClient.ExecuteWithResilienceAsync(req, ResiliencePolicy.Streaming, cancellationToken).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                var totalHeader = resp.Content.Headers.ContentLength;
                var isPartial = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                try
                {
                    if (resp.Headers.ETag != null) etag = resp.Headers.ETag.Tag;
                    if (resp.Content?.Headers?.LastModified.HasValue == true) lastModified = resp.Content.Headers.LastModified;
                }
                catch { }

                var totalExpected = isPartial && totalHeader.HasValue ? existingBytes + totalHeader.Value : (totalHeader ?? 0);
                if (existingBytes > 0 && !isPartial)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    try { if (File.Exists(resumePath)) File.Delete(resumePath); } catch { }
                    existingBytes = 0;
                }

                var fileMode = existingBytes > 0 && isPartial ? FileMode.Append : FileMode.Create;
                using (var content = await HttpContentLightUp.ReadAsStreamAsync(resp.Content, cancellationToken).ConfigureAwait(false))
                using (var fs = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.None, 8192, useAsync: true))
                {
                    long startBytes = existingBytes;
                    await CopyWithProgressAsync(content, fs, totalExpected, cancellationToken, (fraction, bps, eta) =>
                    {
                        ReportProgress(progress, completedBefore, totalTracks, track?.Title, fraction, bps, eta);
                        TryWriteResumeCheckpoint(resumePath, (long)(startBytes + fraction * Math.Max(1, totalExpected)), totalExpected, etag, lastModified?.UtcDateTime);
                    }).ConfigureAwait(false);
                }

                try { FileSystemUtilities.MoveFile(tempPath, outputPath, true); }
                catch { if (File.Exists(outputPath)) File.Delete(outputPath); File.Move(tempPath, outputPath); }
                try { if (File.Exists(resumePath)) File.Delete(resumePath); } catch { }

                var fileSize = new FileInfo(outputPath).Length;
                if (fileSize <= 0)
                {
                    TryDelete(outputPath);
                    TryDelete(resumePath);
                    return new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = "Downloaded file is empty" };
                }

                // Apply metadata tags after successful download
                await ApplyMetadataAsync(outputPath, track, cancellationToken).ConfigureAwait(false);

                return new TrackDownloadResult { TrackId = trackId, Success = true, FilePath = outputPath, FileSize = fileSize, ActualQuality = quality };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = $"Track {trackId}: {ex.Message}" };
            }
        }

        private async Task ApplyMetadataAsync(string filePath, StreamingTrack? track, CancellationToken cancellationToken)
        {
            if (_metadataApplier == null || track == null || string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                await _metadataApplier.ApplyAsync(filePath, track, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Metadata application failure shouldn't fail the download
                // The file is already successfully downloaded - log once per track
                _logger.LogWarning(ex, "[{ServiceName}] Failed to apply metadata to '{FileName}' (track {TrackId})",
                    ServiceName, Path.GetFileName(filePath), track.Id);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        private static async Task CopyWithProgressAsync(Stream input, Stream output, long totalExpected, CancellationToken cancellationToken, Action<double, long, TimeSpan?> onProgress)
        {
            var buffer = new byte[8192];
            long written = 0;
            long windowBytes = 0;
            var interval = 500;
            var last = System.Diagnostics.Stopwatch.StartNew();
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                windowBytes += read;

                if (last.ElapsedMilliseconds >= interval)
                {
                    var seconds = Math.Max(0.001, last.Elapsed.TotalSeconds);
                    var bps = (long)(windowBytes / seconds);
                    var fraction = totalExpected > 0 ? Math.Min(1, (double)written / totalExpected) : 0;
                    TimeSpan? eta = null;
                    if (totalExpected > 0 && bps > 0)
                    {
                        var remain = totalExpected - written;
                        eta = TimeSpan.FromSeconds(Math.Max(0, remain / (double)bps));
                    }
                    onProgress(fraction, bps, eta);
                    windowBytes = 0;
                    last.Restart();
                }
            }

            onProgress(1, 0, TimeSpan.Zero);
        }
        private static void ReportProgress(IProgress<DownloadProgress>? progress, int completed, int total, string? currentTrack, double fractionOfCurrent, long bytesPerSecond, TimeSpan? eta)
        {
            if (progress == null) return;
            var overallPercent = total > 0 ? ((completed + fractionOfCurrent) / Math.Max(1, (double)total)) * 100.0 : 0;
            progress.Report(new DownloadProgress
            {
                CompletedTracks = completed,
                TotalTracks = total,
                PercentComplete = Math.Max(0, Math.Min(100, overallPercent)),
                CurrentTrack = currentTrack,
                EstimatedTimeRemaining = eta,
                BytesPerSecond = bytesPerSecond
            });
        }

        private static void TryWriteResumeCheckpoint(string resumePath, long downloaded, long totalExpected, string? etag, DateTime? lastModifiedUtc)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new ResumeState
                {
                    DownloadedBytes = downloaded,
                    TotalExpectedBytes = totalExpected,
                    ETag = etag,
                    LastModifiedUtc = lastModifiedUtc
                });
                File.WriteAllText(resumePath, json);
            }
            catch { /* best-effort */ }
        }

        private sealed class ResumeState
        {
            public long DownloadedBytes { get; set; }
            public long TotalExpectedBytes { get; set; }
            public string? ETag { get; set; }
            public DateTime? LastModifiedUtc { get; set; }
        }

        public Task<List<StreamingQuality>> GetAvailableQualitiesAsync(string contentId)
        {
            // Not implemented here; services commonly compute this via their APIs
            return Task.FromResult(new List<StreamingQuality>());
        }

        public Task<long> EstimateDownloadSizeAsync(string albumId, StreamingQuality quality = null)
        {
            // Optional: services may implement heuristics based on track counts & quality
            return Task.FromResult(0L);
        }

        public Task CancelDownloadAsync(string downloadId)
        {
            // Out of scope for simple orchestrator
            return Task.CompletedTask;
        }
    }
}
