using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

        // Regex to decode percent-encoded sequences (%XX) — applied once before segment checks.
        private static readonly Regex PercentEncodePattern = new(@"%([0-9A-Fa-f]{2})", RegexOptions.Compiled);

        // All characters that can act as path separators (forward, back, URL-encoded variants).
        private static readonly char[] PathSeparatorChars = { '/', '\\' };

        // Unicode look-alike dots that NFKC-normalise to U+002E (FULL STOP).
        // U+FF0E FULLWIDTH FULL STOP, U+2024 ONE DOT LEADER, U+FE52 SMALL FULL STOP.
        private static readonly char[] UnicodeDotLookalikes = { '．', '․', '﹒' };

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
        /// Path traversal guard with full normalisation.
        ///
        /// Checks that no path segment, after all normalisation passes, is a plain ".."
        /// that would traverse upward. Normalisation applied (in order):
        ///   1. Null-byte / control-character rejection.
        ///   2. Reject extended-length UNC prefix (\\?\).
        ///   3. Percent-decode once (URI %XX → char).
        ///   4. NFKC unicode normalisation (collapses fullwidth dots, etc.).
        ///   5. Replace all separator variants with the platform separator.
        ///   6. Split into segments and reject any segment that is exactly "..".
        ///
        /// Well-formed callers are unaffected: simple relative paths, absolute OS paths,
        /// and paths where ".." appears as a substring of a filename component continue
        /// to be accepted (or rejected) exactly as before.
        /// </summary>
        public static bool IsSafePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;

            // 1. Reject null bytes and ASCII control characters (C0 range except HT/LF/CR).
            foreach (var ch in path)
            {
                if (ch == '\0' || (ch < ' ' && ch != '\t' && ch != '\n' && ch != '\r'))
                    return false;
            }

            // 2. Reject extended-length UNC prefix (\\?\ or //?) — these bypass OS path checks.
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\?/", StringComparison.Ordinal) ||
                path.StartsWith(@"//?/", StringComparison.Ordinal))
            {
                return false;
            }

            // 3. Decode percent-encoded characters once (fold %2e → '.', %2f → '/', etc.).
            var decoded = PercentEncodePattern.Replace(path, m =>
            {
                var hex = m.Groups[1].Value;
                var byteVal = Convert.ToByte(hex, 16);
                return ((char)byteVal).ToString();
            });

            // 4. NFKC Unicode normalisation: collapses fullwidth/compatibility forms.
            decoded = decoded.Normalize(NormalizationForm.FormKC);

            // 4a. Also replace known dot look-alikes that survive NFKC with ASCII dot.
            foreach (var lookalike in UnicodeDotLookalikes)
            {
                decoded = decoded.Replace(lookalike, '.');
            }

            // 5. Normalise separators: replace all variants with a single forward slash for segment splitting.
            decoded = decoded.Replace('\\', '/');

            // 6. Split into segments and check each one.
            var segments = decoded.Split('/', StringSplitOptions.None);
            foreach (var segment in segments)
            {
                // A segment that is exactly ".." is always a traversal, regardless of surrounding context.
                if (segment == "..")
                    return false;
            }

            return true;
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
