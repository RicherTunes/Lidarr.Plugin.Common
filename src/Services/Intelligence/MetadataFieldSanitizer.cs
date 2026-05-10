using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidarr.Plugin.Common.Services.Intelligence
{
    /// <summary>
    /// Music-domain text sanitizer for artist, album and version fields coming from
    /// streaming-service APIs. Performs control-character / HTML-tag stripping,
    /// file-system-safe character substitution and bounded length enforcement.
    /// </summary>
    /// <remarks>
    /// This is the cross-plugin core extracted from per-plugin metadata sanitizers.
    /// Plugin-specific allow-lists (e.g. dangerous-pattern detection that maps to a
    /// service-specific safe default) should remain plugin-local; this class focuses
    /// on the universal music-metadata text pipeline.
    ///
    /// All regex operations use a 250 ms timeout to mitigate ReDoS risk.
    /// </remarks>
    public static class MetadataFieldSanitizer
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        // Control characters and zero-width characters that should be removed.
        // U+200B-U+200D = zero-width chars, U+FEFF = BOM
        private static readonly Regex ControlCharRegex = new(
            "[\x00-\x08\x0B\x0C\x0E-\x1F\x7F​-‍﻿]",
            RegexOptions.Compiled, RegexTimeout);

        // HTML/XML tags that should be stripped
        private static readonly Regex HtmlTagRegex = new(
            @"<[^>]*>", RegexOptions.Compiled, RegexTimeout);

        // Script tags with their content should be completely removed
        private static readonly Regex ScriptTagRegex = new(
            @"<script[^>]*>.*?</script>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);

        private static readonly Regex MultipleUnderscoresRegex = new(@"_{3,}", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex MultipleWhitespaceRegex = new(@"\s+", RegexOptions.Compiled, RegexTimeout);

        /// <summary>Default max length for version fields.</summary>
        public const int DefaultMaxVersionLength = 100;

        /// <summary>Default max length for artist/album/title fields.</summary>
        public const int DefaultMaxFieldLength = 200;

        /// <summary>
        /// Sanitizes a version string (e.g. "Deluxe Edition", "Remastered 2009").
        /// Returns <see cref="string.Empty"/> for null/whitespace input. Does NOT
        /// substitute a default phrase — call sites that want that behavior should
        /// guard against empty results themselves.
        /// </summary>
        public static string SanitizeVersion(string? version, ILogger? logger = null)
        {
            return SanitizeMetadataInternal(version, defaultValue: string.Empty,
                maxLength: DefaultMaxVersionLength, stripHtml: false,
                replaceFsUnsafeChars: true, logger: logger);
        }

        /// <summary>Sanitizes an artist name. Falls back to "Unknown Artist" on empty input.</summary>
        public static string SanitizeArtistName(string? artistName, ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(artistName))
                return "Unknown Artist";
            var s = SanitizeMetadataInternal(artistName, defaultValue: "Unknown Artist",
                maxLength: DefaultMaxFieldLength, stripHtml: true,
                replaceFsUnsafeChars: true, logger: logger);
            return string.IsNullOrWhiteSpace(s) ? "Unknown Artist" : s;
        }

        /// <summary>Sanitizes an album title. Falls back to "Unknown Album" on empty input.</summary>
        public static string SanitizeAlbumTitle(string? albumTitle, ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return "Unknown Album";
            var s = SanitizeMetadataInternal(albumTitle, defaultValue: "Unknown Album",
                maxLength: DefaultMaxFieldLength, stripHtml: true,
                replaceFsUnsafeChars: true, logger: logger);
            return string.IsNullOrWhiteSpace(s) ? "Unknown Album" : s;
        }

        /// <summary>Sanitizes a track/song title.</summary>
        public static string SanitizeTrackTitle(string? trackTitle, ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(trackTitle))
                return "Unknown Track";
            var s = SanitizeMetadataInternal(trackTitle, defaultValue: "Unknown Track",
                maxLength: DefaultMaxFieldLength, stripHtml: true,
                replaceFsUnsafeChars: true, logger: logger);
            return string.IsNullOrWhiteSpace(s) ? "Unknown Track" : s;
        }

        /// <summary>
        /// Generic text-field sanitization with caller-provided defaults and limits.
        /// Returns <paramref name="defaultValue"/> if the result is empty.
        /// </summary>
        public static string SanitizeMetadataField(
            string? input,
            string defaultValue = "",
            int maxLength = DefaultMaxFieldLength,
            bool stripHtml = true,
            ILogger? logger = null)
        {
            return SanitizeMetadataInternal(input, defaultValue, maxLength, stripHtml,
                replaceFsUnsafeChars: true, logger: logger);
        }

        /// <summary>Escapes a string for safe inclusion in HTML content.</summary>
        public static string HtmlEncode(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            return input
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }

        /// <summary>
        /// Heuristic check for path-traversal markers. Plugin-specific dangerous-pattern
        /// allow-lists (XSS / SQLi keywords) should be implemented locally.
        /// </summary>
        public static bool ContainsPathTraversal(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;
            return input.Contains("../", StringComparison.OrdinalIgnoreCase) ||
                   input.Contains("..\\", StringComparison.OrdinalIgnoreCase);
        }

        // ----- Internal pipeline -----

        private static string SanitizeMetadataInternal(
            string? input,
            string defaultValue,
            int maxLength,
            bool stripHtml,
            bool replaceFsUnsafeChars,
            ILogger? logger)
        {
            if (string.IsNullOrWhiteSpace(input))
                return defaultValue;

            var log = logger ?? NullLogger.Instance;
            string sanitized;

            try
            {
                sanitized = ControlCharRegex.Replace(input, "");
                sanitized = ScriptTagRegex.Replace(sanitized, "");
                if (stripHtml)
                {
                    sanitized = HtmlTagRegex.Replace(sanitized, "");
                }
            }
            catch (RegexMatchTimeoutException)
            {
                log.LogWarning("Regex timeout while sanitizing metadata field, returning safe default");
                return defaultValue;
            }

            if (replaceFsUnsafeChars)
            {
                sanitized = sanitized
                    .Replace("..", "_")    // path traversal
                    .Replace("~/", "_")    // home dir
                    .Replace("\\", "_")
                    .Replace("/", "_")
                    .Replace(":", "-")
                    .Replace("*", "_")
                    .Replace("?", "_")
                    .Replace("\"", "'")
                    .Replace("<", "(")
                    .Replace(">", ")")
                    .Replace("|", "_")
                    .Replace("\r", " ")
                    .Replace("\n", " ")
                    .Replace("\t", " ");
            }

            try
            {
                sanitized = MultipleUnderscoresRegex.Replace(sanitized, "___");
                sanitized = MultipleWhitespaceRegex.Replace(sanitized, " ").Trim();
            }
            catch (RegexMatchTimeoutException)
            {
                log.LogWarning("Regex timeout while normalizing whitespace in metadata field, returning safe default");
                return defaultValue;
            }

            if (maxLength > 0 && sanitized.Length > maxLength)
            {
                sanitized = sanitized.Substring(0, maxLength).TrimEnd();
            }

            return string.IsNullOrWhiteSpace(sanitized) ? defaultValue : sanitized;
        }
    }
}
