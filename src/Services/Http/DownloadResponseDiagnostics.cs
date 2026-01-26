using System;
using Lidarr.Plugin.Common.Security;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Diagnostic utilities for HTTP download responses.
    /// Helps detect and log unexpected content types and payloads.
    /// </summary>
    public static class DownloadResponseDiagnostics
    {
        /// <summary>
        /// Extracts the host from a URL for diagnostic logging.
        /// </summary>
        /// <param name="url">The URL to extract host from</param>
        /// <returns>The sanitized host or empty string if extraction fails</returns>
        public static string TryGetHost(string url)
        {
            return Sanitize.UrlHostOnly(url);
        }

        /// <summary>
        /// Determines if a content type header indicates text-based content.
        /// </summary>
        /// <param name="contentType">The content type header value</param>
        /// <returns>True if the content type appears to be text-based</returns>
        public static bool IsTextLikeContentType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return false;
            }

            return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Heuristically detects if a byte buffer looks like text content
        /// by checking for common text prefixes after skipping whitespace.
        /// </summary>
        /// <param name="buffer">The byte buffer to check</param>
        /// <param name="length">The number of bytes to check</param>
        /// <returns>True if the buffer appears to start with text content markers</returns>
        public static bool LooksLikeTextPayload(byte[] buffer, int length)
        {
            var max = Math.Min(length, 32);
            var i = 0;
            while (i < max)
            {
                var b = buffer[i];
                if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
                {
                    break;
                }

                i++;
            }

            if (i >= max)
            {
                return false;
            }

            var first = buffer[i];
            return first == (byte)'<' || first == (byte)'{' || first == (byte)'[';
        }

        /// <summary>
        /// Returns a sanitized version of a text snippet safe for logging.
        /// Removes potential sensitive information while preserving diagnostic value.
        /// </summary>
        /// <param name="snippet">The text snippet to sanitize</param>
        /// <returns>A sanitized version safe for logging, or [empty] if null/whitespace</returns>
        public static string GetSafeSnippetForLogging(string? snippet)
        {
            if (string.IsNullOrWhiteSpace(snippet)) return "[empty]";
            return Sanitize.SafeErrorMessage(snippet);
        }
    }
}
