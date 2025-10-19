using System;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
    internal static class TokenProtectorFactory
    {
        public static ITokenProtector CreateFromEnvironment()
        {
            var mode = (Environment.GetEnvironmentVariable("LP_COMMON_PROTECTOR") ?? "auto").Trim().ToLowerInvariant();
            var appName = Environment.GetEnvironmentVariable("LP_COMMON_APP_NAME") ?? "Lidarr.Plugin.Common";
            var keysPath = Environment.GetEnvironmentVariable("LP_COMMON_KEYS_PATH");
            var certPath = Environment.GetEnvironmentVariable("LP_COMMON_CERT_PATH");
            var certPwd = Environment.GetEnvironmentVariable("LP_COMMON_CERT_PASSWORD");
            var certThumb = Environment.GetEnvironmentVariable("LP_COMMON_CERT_THUMBPRINT");
            var akvKey = Environment.GetEnvironmentVariable("LP_COMMON_AKV_KEY_ID") ?? Environment.GetEnvironmentVariable("LP_COMMON_KMS_URI");

            switch (mode)
            {
                case "dpapi":
                case "dpapi-user":
                    return new DpapiTokenProtector(machineScope: false);
                case "dpapi-machine":
                    return new DpapiTokenProtector(machineScope: true);
                case "keychain":
                    return new KeychainTokenProtector();
                case "secret-service":
                    // Prefer Secret Service when available; otherwise fall back to DP
                    try { return new SecretServiceTokenProtector(); } catch { return DataProtectionTokenProtector.Create(appName, keysPath, certPath, certPwd, certThumb, akvKey); }
                case "dataprotection":
                    return DataProtectionTokenProtector.Create(appName, keysPath, certPath, certPwd, certThumb, akvKey);
                case "auto":
                default:
                    if (OperatingSystem.IsWindows())
                    {
                        // Default to user-scoped DPAPI on Windows for dev/workstation safety
                        return new DpapiTokenProtector(machineScope: false);
                    }
                    if (OperatingSystem.IsMacOS())
                    {
                        try { return new KeychainTokenProtector(); } catch { /* fall through */ }
                    }
                    if (OperatingSystem.IsLinux())
                    {
                        try { return new SecretServiceTokenProtector(); } catch { /* fall through */ }
                    }
                    // Cross-platform default: DataProtection with file keyring; allow optional cert/AKV protection
                    return DataProtectionTokenProtector.Create(appName, keysPath, certPath, certPwd, certThumb, akvKey);
            }
        }
    }
}
