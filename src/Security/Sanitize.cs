using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Lidarr.Plugin.Common.Security
{
    /// <summary>
    /// Context-specific encoding and sanitization helpers.
    /// Use these at the point of use (URL building, file naming, etc.).
    /// </summary>
    public static class Sanitize
    {
        private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON","PRN","AUX","NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
        };

        /// <summary>
        /// Encodes a URL component using RFC 3986 rules.
        /// </summary>
        public static string UrlComponent(string? value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }

        /// <summary>
        /// Produces a safe file/folder name segment for the current OS.
        /// Removes invalid chars, trims, and guards Windows reserved device names.
        /// </summary>
        public static string PathSegment(string? segment)
        {
            if (string.IsNullOrWhiteSpace(segment)) return string.Empty;

            // Remove invalid file name characters
            var invalid = Path.GetInvalidFileNameChars();
            var filtered = new string(segment.Where(c => !invalid.Contains(c)).ToArray());

            // Windows specifics: no trailing spaces/dots, avoid reserved names
            filtered = filtered.Trim().TrimEnd('.');
            if (WindowsReservedDeviceNames.Contains(filtered))
            {
                filtered = "_" + filtered;
            }

            // Keep it reasonable but do not aggressively truncate
            // (Callers can decide if they need length limits)
            return filtered;
        }

        /// <summary>
        /// Basic path traversal guard. Use when accepting relative segments.
        /// </summary>
        public static bool IsSafePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            return !path.Contains("..") && !path.Contains("../") && !path.Contains("..\\");
        }

        /// <summary>
        /// Escapes for HTML rendering. Do not use for URL building.
        /// </summary>
        public static string DisplayText(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return System.Net.WebUtility.HtmlEncode(value);
        }
    }
}

