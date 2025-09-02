using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Generic validation utilities for I/O, formats, and sizes.
    /// </summary>
    public static class ValidationUtilities
    {
        public static bool ValidateDownloadedFile(string filePath, long? expectedSize = null, string expectedHash = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
            var fi = new FileInfo(filePath);
            if (fi.Length == 0) return false;
            if (expectedSize.HasValue && fi.Length != expectedSize.Value) return false;
            if (!string.IsNullOrEmpty(expectedHash))
            {
                var algo = expectedHash.Length > 40 ? "SHA256" : "SHA1";
                var actual = CalculateFileHash(filePath, algo);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        public static bool ValidateDirectoryPath(string directoryPath, bool createIfMissing = false)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return false;
            try
            {
                var fullPath = Path.GetFullPath(directoryPath);
                if (Directory.Exists(fullPath)) return true;
                if (createIfMissing) { Directory.CreateDirectory(fullPath); return true; }
                return false;
            }
            catch { return false; }
        }

        public static bool ValidateFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            try
            {
                Path.GetFullPath(filePath);
                var fileName = Path.GetFileName(filePath);
                var invalid = Path.GetInvalidFileNameChars();
                return !invalid.Any(c => fileName.Contains(c));
            }
            catch { return false; }
        }

        public static bool ValidateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }

        public static bool ValidateEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            // Simple RFC-like validation; plugin-specific validators can extend
            try { var addr = new System.Net.Mail.MailAddress(email); return addr.Address == email; } catch { return false; }
        }

        public static bool IsResponseSizeAcceptable(long? contentLength, long maxBytes = 50 * 1024 * 1024)
        {
            return !contentLength.HasValue || contentLength.Value <= maxBytes;
        }

        private static string CalculateFileHash(string filePath, string algorithm = "SHA256")
        {
            using var stream = File.OpenRead(filePath);
            using HashAlgorithm hasher = algorithm.ToUpperInvariant() switch
            {
                "SHA1" => SHA1.Create(),
                "SHA256" => SHA256.Create(),
                "SHA512" => SHA512.Create(),
                "MD5" => MD5.Create(),
                _ => SHA256.Create()
            };
            var hash = hasher.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}

