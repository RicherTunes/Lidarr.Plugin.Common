using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Abstractions.Models
{
    /// <summary>
    /// Represents an active download job inside a plugin AssemblyLoadContext.
    /// </summary>
    public class StreamingDownloadItem
    {
        public string? Id { get; set; }
        public string? AlbumId { get; set; }
        public StreamingAlbum? Album { get; set; }
        public string? OutputPath { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public double Progress { get; set; }
        public string? CurrentTrack { get; set; }
        public bool IsCompleted { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public StreamingDownloadStatus Status { get; set; }
        public DateTime LastUpdated { get; set; }
        public System.Threading.CancellationTokenSource? CancellationToken { get; set; }
    }

    /// <summary>
    /// Download result for a single track.
    /// </summary>
    public class StreamingDownloadResult
    {
        public bool Success { get; set; }
        public string? FilePath { get; set; }
        public string? ErrorMessage { get; set; }
        public TimeSpan Duration { get; set; }
        public long FileSize { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Download job status.
    /// </summary>
    public enum StreamingDownloadStatus
    {
        Queued,
        Downloading,
        Completed,
        Failed,
        Cancelled
    }
}
