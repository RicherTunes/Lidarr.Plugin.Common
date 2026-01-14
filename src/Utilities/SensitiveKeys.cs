using System;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static class SensitiveKeys
    {
        private static readonly string[] ContainsFragments = new[]
        {
            "token",
            "secret",
            "password",
            "auth",
            "credential",
            "key",
            "session",
            "cookie",
            "signature"
        };

        private static readonly string[] ExactMatches = new[]
        {
            "request_sig",
            "sid",
            "app_secret",
            "client_secret",
            "code",
            "authorization",
            "x-api-key",
            "refresh-token",
            "cookie"
        };

        private static readonly string[] ValueIndicators = new[]
        {
            "access_token",
            "refresh_token",
            "id_token",
            "authorization_code",
            "client_secret",
            "app_secret",
            "user_auth_token",
            "bearer ",
            "authorization:",
            "token=",
            "api_key",
            "apikey",
            "password=",
            "secret=",
            "signature="
        };

        public static bool IsSensitive(string? parameterName)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                return false;
            }

            var lower = parameterName.ToLowerInvariant();
            foreach (var exact in ExactMatches)
            {
                if (string.Equals(lower, exact, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var fragment in ContainsFragments)
            {
                if (lower.Contains(fragment, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool LooksSensitiveValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            // Avoid pathological allocations on huge values (e.g., embedded payloads).
            const int maxScanLength = 4096;
            var candidate = value.Length > maxScanLength ? value.Substring(0, maxScanLength) : value;

            // Attempt to detect secrets embedded in URL-encoded strings (including double-encoding).
            for (var i = 0; i < 2; i++)
            {
                var decoded = TryUnescape(candidate);
                if (decoded == null || string.Equals(decoded, candidate, StringComparison.Ordinal))
                {
                    break;
                }

                candidate = decoded.Length > maxScanLength ? decoded.Substring(0, maxScanLength) : decoded;
            }

            foreach (var indicator in ValueIndicators)
            {
                if (candidate.IndexOf(indicator, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string? TryUnescape(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch
            {
                return null;
            }
        }
    }
}
