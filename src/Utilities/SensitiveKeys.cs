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
            "authorization",
            "x-api-key",
            "refresh-token",
            "cookie"
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
    }
}
