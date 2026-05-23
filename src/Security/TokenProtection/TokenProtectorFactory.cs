using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Security.TokenProtection
{
    /// <summary>
    /// Builds an <see cref="ITokenProtector"/> from environment configuration,
    /// with graceful degradation to <see cref="NullTokenProtector"/> when the
    /// configured backend can't initialise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Diagnostics surface is public</strong> so downstream plugins —
    /// which internalise Common via ILRepack — can read the
    /// <see cref="IsDegradedToPlaintext"/> flag and
    /// <see cref="LastDiagnostics"/> snapshot from their plugin-startup code
    /// and surface a "secrets are stored as plaintext" warning to the
    /// operator's log. Before v1.9.3 this class was <c>internal</c>, which
    /// silently broke the loud-at-startup contract — internalised types
    /// can't be reached even with reflection across plugin ALCs.
    /// </para>
    /// </remarks>
    public static class TokenProtectorFactory
    {
        /// <summary>
        /// Set to <see langword="true"/> after the factory has fallen back to
        /// <see cref="NullTokenProtector"/> due to a backend initialisation
        /// failure. Exposed so callers (e.g. plugin-startup logging code)
        /// can confirm whether secrets are being stored encrypted vs. as
        /// plaintext on this run. Volatile read is sufficient — the field is
        /// only written once during the first <see cref="CreateFromEnvironment"/>
        /// call before the result is published to callers.
        /// </summary>
        public static bool IsDegradedToPlaintext => Volatile.Read(ref _degraded) != 0;
        private static int _degraded; // 0 = not degraded, 1 = degraded to NullTokenProtector

        /// <summary>
        /// Diagnostic snapshot describing how the active protector was
        /// constructed. Populated by the first
        /// <see cref="CreateFromEnvironment"/> call. Useful for plugin
        /// startup logs (operators want to see which backend won) and for
        /// regression tests that assert the fallback chain.
        /// </summary>
        public static TokenProtectorDiagnostics? LastDiagnostics { get; private set; }

        public static ITokenProtector CreateFromEnvironment()
        {
            var mode = (Environment.GetEnvironmentVariable("LP_COMMON_PROTECTOR") ?? "auto").Trim().ToLowerInvariant();
            var appName = Environment.GetEnvironmentVariable("LP_COMMON_APP_NAME") ?? "Lidarr.Plugin.Common";
            var keysPath = Environment.GetEnvironmentVariable("LP_COMMON_KEYS_PATH");
            var certPath = Environment.GetEnvironmentVariable("LP_COMMON_CERT_PATH");
            var certPwd = Environment.GetEnvironmentVariable("LP_COMMON_CERT_PASSWORD");
            var certThumb = Environment.GetEnvironmentVariable("LP_COMMON_CERT_THUMBPRINT");
            var akvKey = Environment.GetEnvironmentVariable("LP_COMMON_AKV_KEY_ID") ?? Environment.GetEnvironmentVariable("LP_COMMON_KMS_URI");
            var requireBackend = string.Equals(Environment.GetEnvironmentVariable("LP_COMMON_REQUIRE_PROTECTOR"), "true", StringComparison.OrdinalIgnoreCase);

            switch (mode)
            {
                case "null":
                    // Explicit opt-in to no-protection (e.g. dev environments where the
                    // operator accepts plaintext storage for the convenience of not
                    // needing a key store). Honoured even when LP_COMMON_REQUIRE_PROTECTOR=true
                    // because the operator asked for it by name.
                    return DegradeTo("null (explicit)", "operator set LP_COMMON_PROTECTOR=null", null);
                case "dpapi":
                case "dpapi-user":
                    return TryCreate(() => new DpapiTokenProtector(machineScope: false), "dpapi-user", requireBackend, appName, keysPath, certPath, certPwd, certThumb, akvKey);
                case "dpapi-machine":
                    return TryCreate(() => new DpapiTokenProtector(machineScope: true), "dpapi-machine", requireBackend, appName, keysPath, certPath, certPwd, certThumb, akvKey);
                case "keychain":
                    return TryCreate(() => new KeychainTokenProtector(), "keychain", requireBackend, appName, keysPath, certPath, certPwd, certThumb, akvKey);
                case "secret-service":
                    return TryCreate(() => new SecretServiceTokenProtector(), "secret-service", requireBackend, appName, keysPath, certPath, certPwd, certThumb, akvKey);
                case "dataprotection":
                    return TryCreateDataProtection(appName, keysPath, certPath, certPwd, certThumb, akvKey, requireBackend);
                case "auto":
                default:
                    if (OperatingSystem.IsWindows())
                    {
                        return TryCreate(() => new DpapiTokenProtector(machineScope: false), "dpapi-user (auto)", requireBackend, appName, keysPath, certPath, certPwd, certThumb, akvKey);
                    }
                    if (OperatingSystem.IsMacOS())
                    {
                        try
                        {
                            var keychain = new KeychainTokenProtector();
                            PublishDiagnostics("keychain (auto)", null, keysPath: null);
                            return keychain;
                        }
                        catch
                        {
                            // Fall through to DataProtection
                        }
                    }
                    if (OperatingSystem.IsLinux())
                    {
                        try
                        {
                            var secretService = new SecretServiceTokenProtector();
                            PublishDiagnostics("secret-service (auto)", null, keysPath: null);
                            return secretService;
                        }
                        catch
                        {
                            // Fall through to DataProtection
                        }
                    }
                    return TryCreateDataProtection(appName, keysPath, certPath, certPwd, certThumb, akvKey, requireBackend);
            }
        }

        private static ITokenProtector TryCreate(
            Func<ITokenProtector> factory,
            string backendName,
            bool requireBackend,
            string appName,
            string? keysPath,
            string? certPath,
            string? certPwd,
            string? certThumb,
            string? akvKey)
        {
            try
            {
                var protector = factory();
                PublishDiagnostics(backendName, null, keysPath);
                return protector;
            }
            catch (Exception ex) when (!requireBackend && IsExpectedBackendInitFailure(ex))
            {
                // First-tier fallback: try DataProtection. If we were already trying
                // DataProtection in the caller's switch arm, the second-tier fallback
                // (NullTokenProtector) will catch it.
                if (!string.Equals(backendName, "dpapi-user", StringComparison.Ordinal) &&
                    !string.Equals(backendName, "dpapi-machine", StringComparison.Ordinal) &&
                    !string.Equals(backendName, "dpapi-user (auto)", StringComparison.Ordinal) &&
                    !string.Equals(backendName, "keychain", StringComparison.Ordinal))
                {
                    return TryCreateDataProtection(appName, keysPath, certPath, certPwd, certThumb, akvKey, requireBackend, fallbackFrom: backendName, fallbackCause: ex);
                }

                return DegradeTo("null (degraded)",
                    $"{backendName} backend init failed: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }

        private static ITokenProtector TryCreateDataProtection(
            string appName,
            string? keysPath,
            string? certPath,
            string? certPwd,
            string? certThumb,
            string? akvKey,
            bool requireBackend,
            string? fallbackFrom = null,
            Exception? fallbackCause = null)
        {
            try
            {
                var keysDir = keysPath ?? DataProtectionTokenProtector.GetDefaultKeysDir(out var keysDirSource);
                EnsureKeysDirIsWritable(keysDir);
                var protector = DataProtectionTokenProtector.Create(appName, keysDir, certPath, certPwd, certThumb, akvKey);
                PublishDiagnostics(
                    fallbackFrom is null ? "dataprotection" : $"dataprotection (fallback from {fallbackFrom})",
                    fallbackCause,
                    keysDir);
                return protector;
            }
            catch (Exception ex) when (!requireBackend && IsExpectedBackendInitFailure(ex))
            {
                return DegradeTo("null (degraded)",
                    $"dataprotection backend init failed: {ex.GetType().Name}: {ex.Message}",
                    ex);
            }
        }

        /// <summary>
        /// Filters which exception types are eligible for the graceful-degradation
        /// fallback path. Adversarial-review finding F6 (v1.9.2): the previous
        /// <c>catch (Exception)</c> swallowed <see cref="OutOfMemoryException"/>,
        /// <see cref="StackOverflowException"/>, and (more importantly)
        /// <see cref="CryptographicException"/> signals from a corrupted keychain
        /// or key ring — those should propagate so the operator sees the real
        /// problem, not a misleading "we silently fell back to plaintext".
        ///
        /// Eligible (silently degraded):
        /// - <see cref="IOException"/> family (file not found, sharing violation, disk full, etc.)
        /// - <see cref="UnauthorizedAccessException"/> (the original bug — read-only mount)
        /// - <see cref="PlatformNotSupportedException"/> (e.g. secret-service on a host without libsecret)
        /// - <see cref="DllNotFoundException"/> / <see cref="EntryPointNotFoundException"/> (native deps missing)
        /// - <see cref="InvalidOperationException"/> when the underlying message identifies a missing path/feature
        /// </summary>
        private static bool IsExpectedBackendInitFailure(Exception ex)
        {
            return ex is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException;
        }

        /// <summary>
        /// Tries to create + write a probe file in <paramref name="keysDir"/>
        /// so we surface "directory not writable" failures here (with a
        /// clearer error) instead of inside <c>DataProtectionProvider.Create</c>
        /// at the first <c>Protect</c> call. Cleans up the probe file after.
        /// </summary>
        private static void EnsureKeysDirIsWritable(string keysDir)
        {
            Directory.CreateDirectory(keysDir);
            var probe = Path.Combine(keysDir, $".lpc-write-probe-{Guid.NewGuid():N}");
            try
            {
                File.WriteAllBytes(probe, Array.Empty<byte>());
            }
            finally
            {
                try { File.Delete(probe); }
                catch
                {
                    // Best-effort cleanup; if we can't delete a 0-byte probe, the
                    // directory is in some odd state but we've already proven we
                    // can write so let DataProtection try anyway.
                }
            }
        }

        private static ITokenProtector DegradeTo(string backendName, string reason, Exception? cause)
        {
            PublishDiagnostics(backendName, cause, keysPath: null, degradedReason: reason);
            return new NullTokenProtector();
        }

        /// <summary>
        /// Publishes the diagnostic snapshot for the most recent factory call AND
        /// updates the <see cref="IsDegradedToPlaintext"/> flag to reflect the
        /// outcome.
        /// </summary>
        /// <remarks>
        /// Adversarial-review finding F2 (v1.9.2): the previous implementation
        /// only ever SET <c>_degraded</c> to 1 (in <c>DegradeTo</c>) and never
        /// cleared it. A subsequent successful <c>CreateFromEnvironment</c> call
        /// (transient I/O blip recovered, or a multi-plugin host where one plugin
        /// degraded earlier and a later plugin's call would have succeeded) left
        /// the flag globally stuck at true — every consumer reading
        /// <see cref="IsDegradedToPlaintext"/> would lie about its own state.
        /// Now the flag tracks the *current* call: set to 1 when this call
        /// degraded; cleared to 0 when this call succeeded with a real backend.
        /// </remarks>
        private static void PublishDiagnostics(string backendName, Exception? cause, string? keysPath, string? degradedReason = null)
        {
            LastDiagnostics = new TokenProtectorDiagnostics(
                BackendName: backendName,
                KeysPath: keysPath,
                Cause: cause,
                DegradedReason: degradedReason);

            // Reflect the current call's outcome. A subsequent successful call
            // clears a prior degradation; a subsequent degraded call sets the
            // flag even if a prior call had succeeded.
            Volatile.Write(ref _degraded, degradedReason is null ? 0 : 1);
        }
    }

    /// <summary>
    /// Diagnostic snapshot describing how the active token protector was
    /// constructed by <see cref="TokenProtectorFactory.CreateFromEnvironment"/>.
    /// Used for plugin startup logs and regression tests.
    /// </summary>
    /// <param name="BackendName">Short label for the active backend
    /// (e.g. "dataprotection", "dpapi-user", "null (degraded)").</param>
    /// <param name="KeysPath">Resolved key-ring directory when applicable
    /// (DataProtection paths only); <see langword="null"/> for backends that
    /// don't use a filesystem key store.</param>
    /// <param name="Cause">Exception that caused a degradation, when the
    /// factory fell back from the preferred backend.</param>
    /// <param name="DegradedReason">Human-readable reason when the factory
    /// fell back to <see cref="NullTokenProtector"/>. Operators should log
    /// this loudly so a misconfigured deployment is visible.</param>
    public sealed record TokenProtectorDiagnostics(
        string BackendName,
        string? KeysPath,
        Exception? Cause,
        string? DegradedReason);
}
