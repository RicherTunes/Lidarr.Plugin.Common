using System;
using System.Security.Cryptography;
using System.Text;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Generic hashing utilities for plugins and shared components.
    /// </summary>
    public static class HashingUtility
    {
        /// <summary>
        /// Computes the MD5 hash of the input string as a lowercase hex string.
        /// Note: MD5 is not suitable for security-sensitive purposes and should
        /// only be used for legacy API requirements (e.g., Qobuz).
        /// </summary>
        public static string ComputeMD5Hash(string input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var md5 = MD5.Create();
            var inputBytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = md5.ComputeHash(inputBytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        /// <summary>
        /// Generates a stable cache key by concatenating components and hashing.
        /// </summary>
        public static string GenerateCacheKey(params string[] components)
        {
            if (components == null || components.Length == 0)
                throw new ArgumentException("At least one component is required", nameof(components));
            var combined = string.Join("|", components);
            return ComputeMD5Hash(combined);
        }

        /// <summary>
        /// Computes the SHA-256 hash of the input string as a lowercase hex string.
        /// </summary>
        public static string ComputeSHA256(string input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var sha = SHA256.Create();
            var inputBytes = Encoding.UTF8.GetBytes(input);
            var hash = sha.ComputeHash(inputBytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Computes HMAC-SHA256 of data using the provided secret, returns lowercase hex.
        /// </summary>
        public static string ComputeHmacSha256(string secret, string data)
        {
            if (secret == null) throw new ArgumentNullException(nameof(secret));
            if (data == null) throw new ArgumentNullException(nameof(data));
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var bytes = Encoding.UTF8.GetBytes(data);
            var mac = hmac.ComputeHash(bytes);
            return Convert.ToHexString(mac).ToLowerInvariant();
        }
    }
}
