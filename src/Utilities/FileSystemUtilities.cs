using System;
using System.IO;
using System.Text;
using System.Linq;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Common file system helpers and naming utilities.
    /// </summary>
    public static class FileSystemUtilities
    {
        public static void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentNullException(nameof(sourcePath));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentNullException(nameof(destinationPath));
            File.Move(sourcePath, destinationPath, overwrite);
        }
        public static string SanitizeFileName(string fileName, int maxLength = 255)
        {
            // Normalize to NFC to avoid cross-OS inconsistencies
            var normalized = (fileName ?? string.Empty).Normalize(NormalizationForm.FormC);
            var sanitized = FileNameSanitizer.SanitizeFileName(normalized);

            // Extra reserved name guard after sanitization (defense in depth)
            var upper = sanitized.ToUpperInvariant();
            var reserved = new System.Collections.Generic.HashSet<string>{
                "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
                "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
            };
            if (reserved.Contains(upper)) sanitized = "_" + sanitized;

            sanitized = sanitized.TrimEnd(' ', '.', '_', '-');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "_";
            }

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
            var normalized = path.Normalize(NormalizationForm.FormC);
            var parts = normalized.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var sanitized = parts.Select(p => SanitizeFileName(p, maxLength));
            return string.Join(Path.DirectorySeparatorChar, sanitized);
        }

        public static string CreateTrackFileName(string title, int trackNumber, string extension = "flac", int maxLength = 200)
        {
            return CreateTrackFileName(title, trackNumber, extension, discNumber: 1, totalDiscs: 1, maxLength);
        }

        public static string CreateTrackFileName(
            string title,
            int trackNumber,
            string extension,
            int discNumber,
            int totalDiscs,
            int maxLength = 200)
        {
            var trackNum = trackNumber.ToString("D2");
            var dn = discNumber > 0 ? discNumber : 1;
            var isMultiDisc = totalDiscs > 1;
            var prefix = isMultiDisc ? $"D{dn:00}T{trackNum}" : trackNum;

            // Normalize extension: trim whitespace, strip leading dots, default to flac
            var safeExtension = (extension ?? string.Empty).Trim().TrimStart('.');
            if (string.IsNullOrEmpty(safeExtension))
                safeExtension = "flac";

            // Leave a bit more headroom to avoid cutting grapheme clusters mid-slice
            var overhead = prefix.Length + 3 + 1 + Math.Max(1, safeExtension.Length);
            var headroom = Math.Max(1, maxLength - overhead);
            var safeTitle = SanitizeFileName(title, headroom);
            return $"{prefix} - {safeTitle}.{safeExtension}";
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
