using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Lidarr.Plugin.Common.Services.Caching
{
    /// <summary>
    /// Stable, deterministic cache key builder for HTTP GET requests.
    /// </summary>
    internal static class CacheKeyBuilder
    {
        /// <summary>
        /// Builds a stable cache key from request components.
        /// </summary>
        /// <param name="method">HTTP method (e.g., GET).</param>
        /// <param name="uri">Full request URI (scheme, host[:port], path).</param>
        /// <param name="canonicalQuery">Canonical query string (sorted ordinal; multivalue keys ordered), without leading '?'.</param>
        /// <param name="authScope">Optional stable scope (already hashed if desired).</param>
        public static string Build(System.Net.Http.HttpMethod method, System.Uri uri, string? canonicalQuery, string? authScope)
        {
            var authority = BuildAuthority(uri);
            var payload = string.Join("\n", new[]
            {
                (method?.Method ?? "GET").Trim().ToUpperInvariant(),
                authority,
                uri.AbsolutePath,              // preserve case in path
                canonicalQuery ?? string.Empty,
                authScope ?? string.Empty
            });

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(payload), hash);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string BuildAuthority(Uri uri)
        {
            var scheme = (uri.Scheme ?? "http").ToLowerInvariant();
            var host = (uri.Host ?? string.Empty).ToLowerInvariant();
            var isDefaultPort = uri.IsDefaultPort || (uri.Port == 80 && scheme == "http") || (uri.Port == 443 && scheme == "https");
            var portPart = isDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return scheme + "://" + host + portPart;
        }
    }
}
