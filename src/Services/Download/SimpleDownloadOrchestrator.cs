using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Models;
using Lidarr.Plugin.Common.Utilities;

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

        public string ServiceName { get; }

        public SimpleDownloadOrchestrator(
            string serviceName,
            HttpClient httpClient,
            Func<string, Task<StreamingAlbum>> getAlbumAsync,
            Func<string, Task<StreamingTrack>> getTrackAsync,
            Func<string, Task<IReadOnlyList<string>>> getAlbumTrackIdsAsync,
            Func<string, StreamingQuality?, Task<(string Url, string Extension)>> getStreamAsync)
        {
            ServiceName = serviceName;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _getAlbumAsync = getAlbumAsync ?? throw new ArgumentNullException(nameof(getAlbumAsync));
            _getTrackAsync = getTrackAsync ?? throw new ArgumentNullException(nameof(getTrackAsync));
            _getAlbumTrackIdsAsync = getAlbumTrackIdsAsync ?? throw new ArgumentNullException(nameof(getAlbumTrackIdsAsync));
            _getStreamAsync = getStreamAsync ?? throw new ArgumentNullException(nameof(getStreamAsync));
        }

        public async Task<DownloadResult> DownloadAlbumAsync(string albumId, string outputDirectory, StreamingQuality quality = null, IProgress<DownloadProgress> progress = null)
        {
            var album = await _getAlbumAsync(albumId).ConfigureAwait(false);
            if (album == null) throw new InvalidOperationException($"Album not found: {albumId}");

            var result = new DownloadResult { Success = true, Duration = TimeSpan.Zero };
            var started = DateTime.UtcNow;
            var files = new List<string>();

            Directory.CreateDirectory(outputDirectory);
            var trackIds = await _getAlbumTrackIdsAsync(albumId).ConfigureAwait(false);
            int done = 0, total = trackIds?.Count ?? album.TrackCount;

            foreach (var trackId in trackIds ?? Array.Empty<string>())
            {
                var track = await _getTrackAsync(trackId).ConfigureAwait(false);
                var trackPath = Path.Combine(outputDirectory, FileSystemUtilities.CreateTrackFileName(track?.Title ?? "Unknown", track?.TrackNumber ?? 0));
                var tr = await DownloadTrackAsync(trackId, trackPath, quality).ConfigureAwait(false);
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
                progress?.Report(new DownloadProgress
                {
                    CompletedTracks = done,
                    TotalTracks = Math.Max(total, trackIds?.Count ?? total),
                    PercentComplete = Math.Max(0, Math.Min(100, (double)done / Math.Max(1, total) * 100.0)),
                    CurrentTrack = track?.Title
                });
            }

            result.FilePaths = files;
            result.TotalSize = files.Where(File.Exists).Select(f => new FileInfo(f).Length).Sum();
            result.Duration = DateTime.UtcNow - started;
            return result;
        }

        public async Task<TrackDownloadResult> DownloadTrackAsync(string trackId, string outputPath, StreamingQuality quality = null)
        {
            var track = await _getTrackAsync(trackId).ConfigureAwait(false);
            if (track == null) throw new InvalidOperationException($"Track not found: {trackId}");

            var (url, extension) = await _getStreamAsync(trackId, quality).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                return new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = "Empty stream URL" };
            }

            if (string.IsNullOrWhiteSpace(Path.GetExtension(outputPath)) && !string.IsNullOrWhiteSpace(extension))
            {
                outputPath = Path.ChangeExtension(outputPath, extension.TrimStart('.'));
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                var tempPath = outputPath + ".partial";
                var resumePath = tempPath + ".resume.json";

                long existingBytes = 0;
                if (File.Exists(tempPath))
                {
                    try { existingBytes = new FileInfo(tempPath).Length; } catch { existingBytes = 0; }
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (existingBytes > 0)
                {
                    req.Headers.Range = new RangeHeaderValue(existingBytes, null);
                }

                using var resp = await _httpClient.ExecuteWithResilienceAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                var totalHeader = resp.Content.Headers.ContentLength;
                var isPartial = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                var totalExpected = isPartial && totalHeader.HasValue ? existingBytes + totalHeader.Value : (totalHeader ?? 0);

                // If server did not honor range, restart cleanly
                if (existingBytes > 0 && !isPartial)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    try { if (File.Exists(resumePath)) File.Delete(resumePath); } catch { }
                    existingBytes = 0;
                }

                var fileMode = existingBytes > 0 && isPartial ? FileMode.Append : FileMode.Create;

                long downloaded = existingBytes;
                using (var content = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var fs = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.None, 8192, useAsync: true))
                {
                    var buffer = new byte[8192];
                    int read;
                    while ((read = await content.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                        downloaded += read;

                        // Opportunistic checkpoint (simple byte count)
                        TryWriteResumeCheckpoint(resumePath, downloaded, totalExpected);
                    }
                    await fs.FlushAsync().ConfigureAwait(false);
                    try { fs.Flush(true); } catch { }
                }

                // Atomic move
                try
                {
                    File.Move(tempPath, outputPath, overwrite: true);
                }
                catch
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Move(tempPath, outputPath);
                }

                // Clean up checkpoint
                try { if (File.Exists(resumePath)) File.Delete(resumePath); } catch { }

                return new TrackDownloadResult
                {
                    TrackId = trackId,
                    Success = true,
                    FilePath = outputPath,
                    FileSize = downloaded,
                    ActualQuality = quality
                };
            }
            catch (Exception ex)
            {
                return new TrackDownloadResult { TrackId = trackId, Success = false, ErrorMessage = ex.Message };
            }
        }

        private static void TryWriteResumeCheckpoint(string resumePath, long downloaded, long totalExpected)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new ResumeState
                {
                    DownloadedBytes = downloaded,
                    TotalExpectedBytes = totalExpected
                });
                File.WriteAllText(resumePath, json);
            }
            catch { /* best-effort */ }
        }

        private sealed class ResumeState
        {
            public long DownloadedBytes { get; set; }
            public long TotalExpectedBytes { get; set; }
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
