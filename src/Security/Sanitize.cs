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

        /// <summary>
        /// Redacts sensitive parts of URLs (query strings, auth tokens) for safe logging.
        /// Use this when including exception messages or URLs in logs/error messages.
        /// </summary>
        /// <param name="text">Text that may contain URLs or sensitive data.</param>
        /// <returns>Text with URLs redacted to scheme://host/path[?...]</returns>
        public static string RedactUrls(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            // Pattern matches http(s) URLs and redacts query strings
            return System.Text.RegularExpressions.Regex.Replace(
                text,
                @"(https?://[^\s?#]+)(\?[^\s]*)?",
                m =>
                {
                    var baseUrl = m.Groups[1].Value;
                    var hasQuery = m.Groups[2].Success && !string.IsNullOrEmpty(m.Groups[2].Value);
                    return hasQuery ? $"{baseUrl}?[REDACTED]" : baseUrl;
                },
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Extracts a URL host for safe logging (no scheme, no path, no query, no credentials).
        /// Returns "unknown" if the input is null/empty/invalid.
        /// </summary>
        public static string UrlHostOnly(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "unknown";

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                return "unknown";
            }

            return uri.Host;
        }

        /// <summary>
        /// Creates a safe error message by redacting URLs and sensitive patterns.
        /// Use this when surfacing exception messages to logs or users.
        /// </summary>
        /// <param name="message">Original error message.</param>
        /// <returns>Sanitized error message safe for logging.</returns>
        public static string SafeErrorMessage(string? message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;

            var redacted = RedactUrls(message);

            // Redact common auth patterns: Bearer tokens, API keys in common formats
            redacted = System.Text.RegularExpressions.Regex.Replace(
                redacted,
                @"(Bearer\s+)[A-Za-z0-9\-_\.]+",
                "$1[REDACTED]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            redacted = System.Text.RegularExpressions.Regex.Replace(
                redacted,
                @"(api[_-]?key[=:]\s*)[A-Za-z0-9\-_]+",
                "$1[REDACTED]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            redacted = System.Text.RegularExpressions.Regex.Replace(
                redacted,
                @"(token[=:]\s*)[A-Za-z0-9\-_\.]+",
                "$1[REDACTED]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return redacted;
        }
    }
}
