using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Lidarr.Plugin.Common.Security
{
    /// <summary>
    /// Input sanitization utilities for all streaming plugins
    /// Prevents injection attacks and ensures safe data handling
    /// UNIVERSAL: All plugins handle user input and API responses
    /// </summary>
    [Obsolete("Use Sanitize.UrlComponent/PathSegment/DisplayText/IsSafePath for context-specific handling.")]
    public static class InputSanitizer
    {
        private static readonly Regex SqlInjectionPattern = new(@"(\b(SELECT|INSERT|UPDATE|DELETE|DROP|CREATE|ALTER|EXEC|UNION|SCRIPT)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ScriptInjectionPattern = new(@"(<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PathTraversalPattern = new(@"(\.\.|\/\.\.|\\\.\.)", RegexOptions.Compiled);

        /// <summary>
        /// Sanitizes search query input to prevent injection attacks
        /// </summary>
        [Obsolete("Avoid generic sanitization of queries. When building URLs use Sanitize.UrlComponent, and avoid HTML-encoding search terms.")]
        public static string SanitizeSearchQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return string.Empty;

            // Remove potentially dangerous patterns
            query = SqlInjectionPattern.Replace(query, "");
            query = ScriptInjectionPattern.Replace(query, "");
            query = PathTraversalPattern.Replace(query, "");

            // Do not HTML-encode here; callers should encode at render-time only.
            return query.Trim();
        }

        /// <summary>
        /// Sanitizes file path to prevent directory traversal
        /// </summary>
        [Obsolete("Use Sanitize.PathSegment for file name parts and perform path join safely.")]
        public static string SanitizeFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return string.Empty;

            // Remove path traversal attempts
            filePath = PathTraversalPattern.Replace(filePath, "");

            // Remove dangerous characters
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars)
            {
                filePath = filePath.Replace(c.ToString(), "");
            }

            return filePath.Trim();
        }

        /// <summary>
        /// Sanitizes API parameter to prevent injection
        /// </summary>
        [Obsolete("Use Sanitize.UrlComponent for query parameter/URL component encoding.")]
        public static string SanitizeApiParameter(string parameter)
        {
            if (string.IsNullOrWhiteSpace(parameter))
                return string.Empty;

            // URL encode for API safety
            return Uri.EscapeDataString(parameter.Trim());
        }

        /// <summary>
        /// Validates that string doesn't contain suspicious patterns
        /// </summary>
        [Obsolete("Use Sanitize.IsSafePath for path traversal checks or validate per-context.")]
        public static bool IsSafeInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return true;

            return !SqlInjectionPattern.IsMatch(input) &&
                   !ScriptInjectionPattern.IsMatch(input) &&
                   !PathTraversalPattern.IsMatch(input);
        }

        /// <summary>
        /// Sanitizes metadata fields to prevent malicious content
        /// </summary>
        [Obsolete("Use Sanitize.DisplayText for HTML display contexts; otherwise avoid altering metadata.")]
        public static string SanitizeMetadata(string metadata)
        {
            if (string.IsNullOrWhiteSpace(metadata))
                return string.Empty;

            // Remove script tags and suspicious content
            metadata = ScriptInjectionPattern.Replace(metadata, "");

            // HTML encode dangerous characters
            metadata = System.Net.WebUtility.HtmlEncode(metadata);

            return metadata.Trim();
        }
    }
}
