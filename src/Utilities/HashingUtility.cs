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
    }
}

