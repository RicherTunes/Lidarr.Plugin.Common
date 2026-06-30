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
using Lidarr.Plugin.Common.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

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
        private readonly IAudioPostProcessor? _postProcessor;
        private readonly IAudioMetadataApplier? _metadataApplier;
        private readonly ILogger _logger;
        private readonly int _maxConcurrentTracks;
        private readonly IDownloadTelemetrySink? _telemetrySink;
        private readonly RemoteMediaUriPolicy _mediaUriPolicy;

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
            ILogger? logger = null,
            IAudioPostProcessor? postProcessor = null,
            RemoteMediaUriPolicy? mediaUriPolicy = null)
        {
            ServiceName = serviceName;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _getAlbumAsync = getAlbumAsync ?? throw new ArgumentNullException(nameof(getAlbumAsync));
            _getTrackAsync = getTrackAsync ?? throw new ArgumentNullException(nameof(getTrackAsync));
            _getAlbumTrackIdsAsync = getAlbumTrackIdsAsync ?? throw new ArgumentNullException(nameof(getAlbumTrackIdsAsync));
            _getStreamAsync = getStreamAsync ?? throw new ArgumentNullException(nameof(getStreamAsync));
            _maxConcurrentTracks = Math.Max(1, maxConcurrentTracks);
            _streamProvider = streamProvider;
            _postProcessor = postProcessor;
            _metadataApplier = metadataApplier ?? new TagLibAudioMetadataApplier();
            _logger = logger ?? NullLogger.Instance;
            _telemetrySink = null;
            _mediaUriPolicy = mediaUriPolicy ?? RemoteMediaUriPolicy.Strict;
        }

        public SimpleDownloadOrchestrator(
            string serviceName,
            HttpClient httpClient,
            Func<string, Task<StreamingAlbum>> getAlbumAsync,
            Func<string, Task<StreamingTrack>> getTrackAsync,
            Func<string, Task<IReadOnlyList<string>>> getAlbumTrackIdsAsync,
            Func<string, StreamingQuality?, Task<(string Url, string Extension)>> getStreamAsync,
            int maxConcurrentTracks,
            IAudioStreamProvider? streamProvider,
            IAudioMetadataApplier? metadataApplier,
            ILogger? logger,
            IAudioPostProcessor? postProcessor,
            IDownloadTelemetrySink? telemetrySink,
            RemoteMediaUriPolicy? mediaUriPolicy = null)
            : this(serviceName, httpClient, getAlbumAsync, getTrackAsync, getAlbumTrackIdsAsync, getStreamAsync, maxConcurrentTracks, streamProvider, metadataApplier, logger, postProcessor, mediaUriPolicy)
        {
            _telemetrySink = telemetrySink;
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

                    var tr = await DownloadTrackInternalAsync(albumId, trackId, track, trackPath, quality, progress, done, total, cancellationToken).ConfigureAwait(false);
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
                        var tr = await DownloadTrackInternalAsync(albumId, trackId, track, trackPath, quality, progress, currentCompleted, total, cancellationToken).ConfigureAwait(false);

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
            // Delegate the completeness decision to Common's canonical AlbumCompletionPolicy:
            // an album is successful ONLY when every track lands (successfulTracks == totalTracks).
            // Incomplete => the host reports Failed so Lidarr can fall back to another source instead
            // of importing a partial album that NoMissingOrUnmatchedTracksSpecification permanently
            // rejects ("Has missing tracks"). Equivalent to the prior failures.Count > 0 gate.
            var successfulTracks = result.TrackResults.Count - failures.Count;
            if (!AlbumCompletionPolicy.IsAlbumDownloadSuccessful(result.TrackResults.Count, successfulTracks))
            {
                result.Success = false;
                result.ErrorMessage = failures.Count > 0
                    ? $"Failed to download {failures.Count}/{result.TrackResults.Count} tracks. First error: {failures[0].ErrorMessage}"
                    : $"Incomplete album: only {successfulTracks}/{result.TrackResults.Count} tracks downloaded.";
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
            return await DownloadTrackInternalAsync(albumId: null, trackId, track, outputPath, quality, null, 0, 1, cancellationToken).ConfigureAwait(false);
        }

        private async Task<TrackDownloadResult> DownloadTrackInternalAsync(string? albumId, string trackId, StreamingTrack track, string outputPath, StreamingQuality? quality, IProgress<DownloadProgress>? progress, int completedBefore, int totalTracks, CancellationToken cancellationToken)
        {
            var counters = new DownloadTelemetryCounters();
            using var telemetryScope = DownloadTelemetryContext.Begin(counters);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                TrackDownloadResult result;

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
                            result = new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = "Downloaded file is empty" };
                        }
                        else
                        {
                            outputPath = await PostProcessAsync(outputPath, track, quality, cancellationToken).ConfigureAwait(false);
                            fileSize = new FileInfo(outputPath).Length;

                            // Apply metadata tags after successful download
                            await ApplyMetadataAsync(outputPath, track, cancellationToken).ConfigureAwait(false);

                            result = new TrackDownloadResult { TrackId = trackId, Success = true, FilePath = outputPath, FileSize = fileSize, ActualQuality = quality };
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Genuine caller cancellation: let the outer catch emit cancellation telemetry
                        // and rethrow. A NON-caller OCE (a per-request provider/HttpClient timeout →
                        // TaskCanceledException with the caller token NOT cancelled) falls through to the
                        // generic catch below and becomes a single-track failure — it must NOT escape and
                        // cancel the whole album.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result = new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = $"Track {trackId}: {Sanitize.SafeErrorMessage(ex.Message)}" };
                    }
                }
                else
                {
                    // Fallback to URL-based path with resume tracking
                    result = await DownloadViaUrlAsync(trackId, track, outputPath, quality, progress, completedBefore, totalTracks, cancellationToken).ConfigureAwait(false);
                }

                stopwatch.Stop();
                EmitTrackTelemetry(albumId, track, quality, trackId, result, stopwatch.Elapsed, counters);
                return result;
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                EmitTrackTelemetry(albumId, track, quality, trackId, new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = "Canceled" }, stopwatch.Elapsed, counters);
                throw;
            }
        }

        private void EmitTrackTelemetry(string? albumId, StreamingTrack? track, StreamingQuality? quality, string trackId, TrackDownloadResult result, TimeSpan elapsed, DownloadTelemetryCounters counters)
        {
            if (_telemetrySink == null)
            {
                return;
            }

            var bytesWritten = Math.Max(0, result.FileSize);

            try
            {
                // Enrich centrally from the track/quality the orchestrator already holds, so every
                // plugin's sink receives artist/album/track/format/quality identically. The explicit
                // albumId/trackId arguments stay authoritative (the model's Id may be unset or differ
                // from the requested id), so they are re-applied over From()'s derived values.
                var telemetry = DownloadTelemetry.From(
                    serviceName: ServiceName,
                    success: result.Success,
                    track: track,
                    album: track?.Album,
                    quality: result.ActualQuality ?? quality,
                    bytesWritten: bytesWritten,
                    elapsed: elapsed,
                    outputPath: result.FilePath,
                    retryCount: counters.RetryCount,
                    tooManyRequestsCount: counters.TooManyRequestsCount,
                    errorMessage: result.ErrorMessage) with { AlbumId = albumId, TrackId = trackId };

                _telemetrySink.OnTrackCompleted(telemetry);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[{ServiceName}] Download telemetry sink failed for track {TrackId}", ServiceName, trackId);
            }
        }


        private async Task<TrackDownloadResult> DownloadViaUrlAsync(string trackId, StreamingTrack track, string outputPath, StreamingQuality? quality, IProgress<DownloadProgress>? progress, int completedBefore, int totalTracks, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (url, extension) = await _getStreamAsync(trackId, quality).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url)) return new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = "Empty stream URL" };

            // SSRF guard: validate the resolved stream URL before fetch (provider-controlled).
            var uriGuard = RemoteMediaUriGuard.Validate(url, _mediaUriPolicy);
            if (!uriGuard.IsAllowed) return new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = $"Unsafe stream URL: {uriGuard.Reason}" };

            if (!string.IsNullOrWhiteSpace(extension)) outputPath = Path.ChangeExtension(outputPath, extension.TrimStart('.'));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                cancellationToken.ThrowIfCancellationRequested();

                var tempPath = outputPath + ".partial";
                var resumePath = tempPath + ".resume.json";
                var maxAttempts = Math.Max(1, MaxDownloadAttempts);

                // Retry-with-resume. A mid-body truncation (HttpIOException "response ended prematurely"
                // / ResponseEnded, or a transport reset) throws DURING the body copy below — AFTER
                // ExecuteWithResilienceAsync has already returned the response headers — so the resilience
                // layer (which only retries the header fetch) never sees it. Without a retry, one truncated
                // track fails the whole album and the host re-grabs into an infinite loop (observed live on
                // Qobuz). Each attempt re-reads the preserved ".partial" and issues a Range request, so the
                // download resumes from where it broke instead of restarting. The atomic move + validation
                // run only after a complete copy (outside the loop).
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
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

                        // LOOP-004: keep the SSRF policy in force across the resilience layer's redirect-following — the
                        // initial URL is validated above, but a 3xx could otherwise bounce to an internal host.
                        using var resp = await _httpClient.ExecuteWithResilienceAsync(
                            req,
                            ResiliencePolicy.Streaming,
                            cancellationToken,
                            validateRedirectTarget: u => RemoteMediaUriGuard.Validate(u, _mediaUriPolicy).IsAllowed).ConfigureAwait(false);
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

                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Only a genuine caller cancellation propagates. A non-caller OCE (per-request
                        // HttpClient timeout → TaskCanceledException, token not cancelled) falls through to
                        // the transient classifier below and is RETRIED (then fails the track) — it must not
                        // be mistaken for user cancellation.
                        throw;
                    }
                    catch (Exception ex) when (attempt < maxAttempts && IsTransientDownloadException(ex, cancellationToken))
                    {
                        // Telemetry: count the resume-retry (non-429 so it doesn't inflate the 429 counter).
                        DownloadTelemetryContext.RecordRetry(System.Net.HttpStatusCode.ServiceUnavailable);
                        _logger.LogWarning(ex,
                            "[{ServiceName}] Transient download failure for track {TrackId} (attempt {Attempt}/{MaxAttempts}); resuming from partial after backoff",
                            ServiceName, trackId, attempt, maxAttempts);
                        var delay = GetRetryDelay(attempt);
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        }
                    }
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

                outputPath = await PostProcessAsync(outputPath, track, quality, cancellationToken).ConfigureAwait(false);
                fileSize = new FileInfo(outputPath).Length;

                // Apply metadata tags after successful download
                await ApplyMetadataAsync(outputPath, track, cancellationToken).ConfigureAwait(false);

                return new TrackDownloadResult { TrackId = trackId, Success = true, FilePath = outputPath, FileSize = fileSize, ActualQuality = quality };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Includes a non-caller OCE that exhausted the resume-retries (e.g. repeated request
                // timeouts) — recorded as a track failure rather than escaping as a whole-album cancel.
                return new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = $"Track {trackId}: {Sanitize.SafeErrorMessage(ex.Message)}" };
            }
        }

        /// <summary>Max attempts for a single track's URL download before giving up. Overridable for tests.</summary>
        internal virtual int MaxDownloadAttempts => 4;

        /// <summary>Backoff between resume-retries (1s, 2s, 4s, capped at 8s). Overridable to zero in tests.</summary>
        internal virtual TimeSpan GetRetryDelay(int attempt) =>
            TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, Math.Max(0, attempt - 1))));

        /// <summary>
        /// Classifies an exception thrown during a URL download attempt as transient (worth a resume-retry).
        /// A requested cancellation is never transient. Covers mid-body truncations (HttpIOException
        /// "ResponseEnded"), connection resets, DNS blips, and per-request timeouts.
        /// </summary>
        private static bool IsTransientDownloadException(Exception ex, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return ex switch
            {
                HttpIOException => true,                       // e.g. ResponseEnded (truncated body)
                HttpRequestException => true,                  // connection reset / DNS / 5xx surfaced by EnsureSuccess
                System.Net.Sockets.SocketException => true,    // transport-level reset
                TaskCanceledException => true,                 // per-request HttpClient timeout (token not cancelled — checked above)
                IOException => true,                           // stream copy interrupted
                _ => false,
            };
        }

        private async Task<string> PostProcessAsync(string filePath, StreamingTrack track, StreamingQuality? quality, CancellationToken cancellationToken)
        {
            if (_postProcessor == null || string.IsNullOrWhiteSpace(filePath) || track == null)
            {
                return filePath;
            }

            try
            {
                string processedPath = await _postProcessor.PostProcessAsync(filePath, track, quality, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(processedPath) || !File.Exists(processedPath))
                {
                    return filePath;
                }

                if (!string.Equals(processedPath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(filePath);
                }

                return processedPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Post-processing is best-effort: a failure (incl. a non-caller timeout OCE) must not
                // discard the already-downloaded file — fall back to the unprocessed original.
                _logger.LogWarning(ex, "Post-processing failed for track {TrackId}: {FilePath}", track.Id, filePath);
                return filePath;
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Metadata application failure shouldn't fail the download (incl. a non-caller timeout
                // OCE). The file is already successfully downloaded - log once per track.
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
            const int BufferSize = 64 * 1024;
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
            long written = 0;
            long windowBytes = 0;
            var interval = 500;
            var last = System.Diagnostics.Stopwatch.StartNew();
            int read;
            try
            {
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
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
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
