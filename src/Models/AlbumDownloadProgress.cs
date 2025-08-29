using System;

namespace Lidarr.Plugin.Common.Models
{
    /// <summary>
    /// Progress information for album download operations
    /// </summary>
    public class AlbumDownloadProgress
    {
        public int CompletedTracks { get; set; }
        public int TotalTracks { get; set; }
        public string CurrentTrack { get; set; } = string.Empty;
        public double OverallPercentage => TotalTracks > 0 ? (double)CompletedTracks / TotalTracks * 100 : 0;
        public long TotalBytesDownloaded { get; set; }
        public TimeSpan EstimatedTimeRemaining { get; set; }
    }
}