using System;

namespace Lidarr.Plugin.Common.CLI.Models
{
    /// <summary>
    /// Represents a download item in the CLI queue
    /// </summary>
    public class CliDownloadItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string AlbumId { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public DownloadStatus Status { get; set; }
        public int ProgressPercent { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public DateTime? AddedDate { get; set; }
        public DateTime? StartedDate { get; set; }
        public DateTime? CompletedDate { get; set; }
        public TimeSpan? EstimatedTimeRemaining { get; set; }
        public string Quality { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
    }

    /// <summary>
    /// Status of a download item
    /// </summary>
    public enum DownloadStatus
    {
        Pending,
        Downloading,
        Completed,
        Failed,
        Cancelled,
        Paused
    }
}