using System;
using System.IO;
using System.Linq;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Common file system helpers and naming utilities.
    /// </summary>
    public static class FileSystemUtilities
    {
        public static string SanitizeFileName(string fileName, int maxLength = 255)
        {
            var sanitized = FileNameSanitizer.SanitizeFileName(fileName);
            if (sanitized.Length > maxLength)
            {
                sanitized = sanitized[..maxLength];
                var lastSpace = sanitized.LastIndexOf(' ');
                if (lastSpace > maxLength / 2)
                {
                    sanitized = sanitized[..lastSpace];
                }
                sanitized = sanitized.TrimEnd(' ', '.', '_', '-');
            }
            return sanitized;
        }

        public static string SanitizeDirectoryPath(string path, int maxLength = 255)
        {
            if (string.IsNullOrWhiteSpace(path)) return "Unknown";
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var sanitized = parts.Select(p => SanitizeFileName(p, maxLength));
            return string.Join(Path.DirectorySeparatorChar, sanitized);
        }

        public static string CreateTrackFileName(string title, int trackNumber, string extension = "flac", int maxLength = 200)
        {
            var trackNum = trackNumber.ToString("D2");
            var safeTitle = SanitizeFileName(title, Math.Max(1, maxLength - trackNum.Length - 4));
            return $"{trackNum} - {safeTitle}.{extension}";
        }

        public static string CreateAlbumDirectoryName(string albumTitle, int? year = null, int maxLength = 200)
        {
            var yearSuffix = year.HasValue ? $" ({year})" : string.Empty;
            var available = Math.Max(1, maxLength - yearSuffix.Length);
            var safeTitle = SanitizeFileName(albumTitle, available);
            return $"{safeTitle}{yearSuffix}";
        }
    }
}

