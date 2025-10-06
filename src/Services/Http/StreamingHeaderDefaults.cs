using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Single source of truth for default streaming HTTP headers.
    /// </summary>
    internal static class StreamingHeaderDefaults
    {
        public static void ApplyTo(HttpRequestMessage request, string? userAgent)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            TryAdd(request.Headers, "Accept", "application/json");
            TryAdd(request.Headers, "Accept-Language", "en-US,en;q=0.9");
            if (!string.IsNullOrWhiteSpace(userAgent)) TryAdd(request.Headers, "User-Agent", userAgent!);
            // Intentionally do not set Accept-Encoding here; rely on handler AutomaticDecompression
        }

        public static void ApplyTo(IDictionary<string, string> headers, string? userAgent)
        {
            if (headers == null) throw new ArgumentNullException(nameof(headers));

            headers["Accept"] = "application/json";
            headers["Accept-Language"] = "en-US,en;q=0.9";
            if (!string.IsNullOrWhiteSpace(userAgent)) headers["User-Agent"] = userAgent!;
        }

        private static void TryAdd(System.Net.Http.Headers.HttpRequestHeaders headers, string name, string value)
        {
            try { headers.TryAddWithoutValidation(name, value); } catch { }
        }
    }
}

