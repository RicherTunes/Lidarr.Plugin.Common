using System;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Helper utilities for download response diagnostics and error reporting.
    /// Provides Content-Type sniffing, sensitive data redaction, and URL parsing.
    /// </summary>
    public static class DownloadDiagnostics
    {
        /// <summary>
        /// Extracts the host from a URL for diagnostic messages.
        /// Returns "unknown" if the URL is invalid.
        /// </summary>
        public static string TryGetHost(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "unknown";
            }

            try
            {
                return new Uri(url).Host;
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Checks if a Content-Type header indicates text content (not audio).
        /// </summary>
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
        /// Checks if the payload looks like text content (HTML, JSON, XML).
        /// Convenience overload for byte array + length (delegates to DownloadPayloadValidator).
        /// </summary>
        public static bool LooksLikeTextPayload(byte[] buffer, int length)
        {
            if (buffer == null || length <= 0)
            {
                return false;
            }

            return DownloadPayloadValidator.LooksLikeTextPayload(buffer.AsSpan(0, Math.Min(length, buffer.Length)));
        }

        /// <summary>
        /// Checks if a Content-Type header indicates audio content.
        /// </summary>
        public static bool IsAudioContentType(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return false;
            }

            return contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("application/x-flac", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines if a text snippet should be redacted from error messages
        /// because it may contain sensitive information.
        /// </summary>
        public static bool ShouldRedactSnippet(string? snippet)
        {
            if (string.IsNullOrWhiteSpace(snippet))
            {
                return false;
            }

            // Check for common sensitive patterns
            return snippet.Contains("http", StringComparison.OrdinalIgnoreCase) ||
                   snippet.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                   snippet.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                   snippet.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                   snippet.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                   snippet.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
                   snippet.Contains("user_auth_token", StringComparison.OrdinalIgnoreCase) ||
                   snippet.Contains("app_secret", StringComparison.OrdinalIgnoreCase) ||
                   snippet.Contains("bearer", StringComparison.OrdinalIgnoreCase) ||
                   snippet.Contains("authorization", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates a safe snippet preview from raw bytes for error messages.
        /// Automatically redacts sensitive content.
        /// </summary>
        /// <param name="buffer">Buffer containing the first bytes of the download</param>
        /// <param name="length">Number of bytes to consider</param>
        /// <param name="maxLength">Maximum snippet length (default 512)</param>
        /// <returns>Safe snippet for error messages, or "[redacted]" if sensitive</returns>
        public static string CreateSafeSnippet(byte[] buffer, int length, int maxLength = 512)
        {
            if (buffer == null || length <= 0)
            {
                return "[empty]";
            }

            var actualLength = Math.Min(length, Math.Min(buffer.Length, maxLength));
            var snippet = System.Text.Encoding.UTF8.GetString(buffer, 0, actualLength)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            return ShouldRedactSnippet(snippet) ? "[redacted]" : snippet;
        }

        /// <summary>
        /// Formats a download error message with diagnostic context.
        /// </summary>
        public static string FormatDownloadError(string message, string? url, string? contentType, long? contentLength)
        {
            var host = TryGetHost(url);
            var ct = contentType ?? "unknown";
            var cl = contentLength?.ToString() ?? "unknown";
            return $"{message} (Host={host}, Content-Type={ct}, Content-Length={cl})";
        }
    }
}
