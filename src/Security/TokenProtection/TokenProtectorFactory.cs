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

        /// <summary>
        /// Fires whenever <see cref="CreateFromEnvironment"/> publishes a
        /// degradation diagnostic — i.e. when the active call falls back to
        /// <see cref="NullTokenProtector"/> because no real backend could
        /// initialise. Subscribers can surface the warning beyond the static
        /// snapshot (adversarial-review F8). Adding this event is additive;
        /// plugins that already poll <see cref="IsDegradedToPlaintext"/> at
        /// startup don't need to change.
        /// </summary>
        /// <remarks>
        /// Subscribers MUST be exception-safe. The event invocation is
        /// inside <c>PublishDiagnostics</c>, on the same thread as
        /// <c>CreateFromEnvironment</c>; an exception from a subscriber will
        /// propagate to the caller. Static-event subscription has the usual
        /// lifetime risk: if a long-lived subscriber holds plugin-scoped
        /// state, the plugin's ALC can't unload until the subscription is
        /// removed.
        /// </remarks>
        public static event Action<TokenProtectorDiagnostics>? DegradationDetected;

        /// <summary>
        /// One-shot warning logger for plugins to call from credential-handling
        /// hot paths (<c>set_ApiKey</c>, settings save, etc.). At-most-once per
        /// process lifetime: subsequent calls are silent no-ops. Returns
        /// <see langword="true"/> if this call emitted a warning,
        /// <see langword="false"/> otherwise.
        /// </summary>
        /// <param name="logWarning">Sink that emits the formatted warning line
        /// to the operator's log. Plugin code typically passes
        /// <c>logger.Warn</c> or equivalent.</param>
        /// <remarks>
        /// Adversarial-review F8: the diagnostics surface in v1.9.2/v1.9.3 was
        /// a static snapshot — plugins had to read it at startup explicitly.
        /// This helper closes the gap so a degraded session can also be
        /// surfaced from the path the operator actually exercises (when they
        /// try to save a credential). It logs at most once because the
        /// alternative (per-call) would spam the log.
        /// </remarks>
        public static bool LogDegradationOnce(Action<string> logWarning)
        {
            if (logWarning is null) return false;
            if (!IsDegradedToPlaintext) return false;
            if (Interlocked.CompareExchange(ref _degradationWarningEmitted, 1, 0) != 0) return false;

            var diag = LastDiagnostics;
            var reason = diag?.DegradedReason ?? "(reason unavailable)";
            var keysPath = diag?.KeysPath ?? "(in-memory)";
            logWarning(
                $"Lidarr.Plugin.Common token protection is DEGRADED — secrets are being stored as PLAINTEXT. " +
                $"Reason: {reason}. " +
                $"To fix: set LP_COMMON_KEYS_PATH to a writable directory (e.g. /config/.lidarr-keys) and restart Lidarr. " +
                $"To opt into hard-failure instead of plaintext fallback, set LP_COMMON_REQUIRE_PROTECTOR=true.");
            return true;
        }

        private static int _degradationWarningEmitted; // 0 = not yet, 1 = already

        /// <summary>
        /// Probe payload written by <see cref="EnsureKeysDirIsWritable"/>.
        /// Spells out "LPC-PROBE" in UTF-8 so a probe file left behind by
        /// a crashed process is identifiable by an operator inspecting the
        /// dir. Non-empty (8 bytes) so write-then-truncate filesystems
        /// can't pass the probe falsely (F4).
        /// </summary>
        private static readonly byte[] ProbePayload = new byte[] { 0x4C, 0x50, 0x43, 0x2D, 0x50, 0x52, 0x4F, 0x42, 0x45 };

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
            // Adversarial-review F5 fix: walk the candidate chain. Previously,
            // GetDefaultKeysDir returned the FIRST rooted candidate; if that
            // candidate was rooted but unwritable, the factory immediately
            // degraded to NullTokenProtector instead of trying the next
            // candidate in the chain. Now we iterate every candidate and
            // probe-write each; the first one that passes wins.
            //
            // When the caller supplied LP_COMMON_KEYS_PATH, that override
            // wins exclusively — we don't iterate past it, because the
            // operator explicitly asked for that location and silently
            // re-routing to a different one would surprise them.

            if (!string.IsNullOrWhiteSpace(keysPath))
            {
                return TryCreateDataProtectionAt(keysPath!, "LP_COMMON_KEYS_PATH (operator override)", appName, certPath, certPwd, certThumb, akvKey, requireBackend, fallbackFrom, fallbackCause);
            }

            Exception? lastFailure = null;
            string? lastFailedSource = null;
            foreach (var (path, source) in DataProtectionTokenProtector.EnumerateKeysDirCandidates())
            {
                try
                {
                    EnsureKeysDirIsWritable(path);
                    var protector = DataProtectionTokenProtector.Create(appName, path, certPath, certPwd, certThumb, akvKey);
                    PublishDiagnostics(
                        fallbackFrom is null ? $"dataprotection ({source})" : $"dataprotection (fallback from {fallbackFrom}; via {source})",
                        fallbackCause,
                        path);
                    return protector;
                }
                catch (Exception ex) when (IsExpectedBackendInitFailure(ex))
                {
                    // F5: this candidate is rooted but unwritable / unusable.
                    // Continue to the next candidate instead of degrading.
                    lastFailure = ex;
                    lastFailedSource = source;
                }
            }

            // Every candidate failed. Either every backend is unavailable
            // OR the caller is on a host with extreme write restrictions
            // (the probe-write to Path.GetTempPath() at the end of the chain
            // should be near-impossible to fail, but defensive code regardless).
            if (requireBackend)
            {
                throw lastFailure ?? new InvalidOperationException("No writable DataProtection key dir found.");
            }
            return DegradeTo("null (degraded)",
                $"dataprotection backend init failed for every candidate (last: {lastFailedSource}): {lastFailure?.GetType().Name}: {lastFailure?.Message}",
                lastFailure);
        }

        private static ITokenProtector TryCreateDataProtectionAt(
            string keysDir,
            string source,
            string appName,
            string? certPath,
            string? certPwd,
            string? certThumb,
            string? akvKey,
            bool requireBackend,
            string? fallbackFrom,
            Exception? fallbackCause)
        {
            try
            {
                EnsureKeysDirIsWritable(keysDir);
                var protector = DataProtectionTokenProtector.Create(appName, keysDir, certPath, certPwd, certThumb, akvKey);
                PublishDiagnostics(
                    fallbackFrom is null ? $"dataprotection ({source})" : $"dataprotection (fallback from {fallbackFrom}; via {source})",
                    fallbackCause,
                    keysDir);
                return protector;
            }
            catch (Exception ex) when (!requireBackend && IsExpectedBackendInitFailure(ex))
            {
                return DegradeTo("null (degraded)",
                    $"dataprotection backend init failed at {source}: {ex.GetType().Name}: {ex.Message}",
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
            if (ex is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException
                or System.ComponentModel.Win32Exception)
            {
                return true;
            }
            // SecretServiceTokenProtector wraps Win32Exception ("process not found"
            // when /usr/bin/secret-tool is absent from the host, e.g. on a stock
            // ubuntu-latest GitHub Actions runner) in an InvalidOperationException.
            // Walk the inner-exception chain so the factory falls back to
            // DataProtection instead of leaking the wrapper exception to callers.
            return ex.InnerException is not null && IsExpectedBackendInitFailure(ex.InnerException);
        }

        /// <summary>
        /// Reserved system paths the factory refuses to use even if the
        /// operator supplied them via <c>LP_COMMON_KEYS_PATH</c>. Writing
        /// secrets into these directories — even probe files — would be a
        /// serious misconfiguration the factory should not silently honour.
        /// Adversarial-review F4 (v1.9.2): the previous
        /// <c>EnsureKeysDirIsWritable</c> happily probed any rooted path.
        /// </summary>
        private static readonly string[] ForbiddenSystemPaths = OperatingSystem.IsWindows()
            ? new[]
            {
                @"\Windows\",
                @"\Windows\System32\",
                @"\ProgramData\Microsoft\Crypto\",
            }
            : new[]
            {
                "/etc/",
                "/proc/",
                "/sys/",
                "/dev/",
                "/boot/",
                "/usr/bin/",
                "/usr/sbin/",
                "/bin/",
                "/sbin/",
            };

        /// <summary>
        /// Tries to create + write + read-back a probe file in
        /// <paramref name="keysDir"/> so we surface "directory not writable"
        /// failures here (with a clearer error) instead of inside
        /// <c>DataProtectionProvider.Create</c> at the first <c>Protect</c>
        /// call. Cleans up the probe file after.
        /// </summary>
        /// <remarks>
        /// Adversarial-review F4 hardening (v1.9.3 → v1.9.4):
        /// <list type="bullet">
        ///   <item><description>Refuses well-known system paths so an
        ///     operator typo (<c>LP_COMMON_KEYS_PATH=/etc</c>) doesn't
        ///     silently scribble a probe file into critical system dirs.</description></item>
        ///   <item><description>Writes a non-empty payload (<see cref="ProbePayload"/>)
        ///     instead of a 0-byte file. Some POSIX overlay/sshfs filesystems
        ///     happily accept 0-byte writes but fail on real content, so the
        ///     probe must mirror what DataProtection actually does.</description></item>
        ///   <item><description>Uses <c>FileMode.CreateNew</c> +
        ///     <c>FileShare.None</c> so the probe can't be hijacked by a
        ///     concurrent process between create and delete.</description></item>
        ///   <item><description>Always cleans up the probe on the way out,
        ///     even if the inner write or read-back throws.</description></item>
        ///   <item><description>Reads the probe back and verifies the payload
        ///     round-tripped — guards against write-then-truncate
        ///     filesystems (some NFS configurations) that would happily
        ///     accept the open + write calls but discard content.</description></item>
        /// </list>
        /// </remarks>
        private static void EnsureKeysDirIsWritable(string keysDir)
        {
            // Refuse forbidden system paths even if the operator supplied them.
            // The normalisation appends a trailing separator so prefix-match
            // is anchored at the directory boundary (not a substring match).
            var normalised = Path.TrimEndingDirectorySeparator(Path.GetFullPath(keysDir)) + Path.DirectorySeparatorChar;
            foreach (var forbidden in ForbiddenSystemPaths)
            {
                if (normalised.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException(
                        $"Refusing to use '{keysDir}' for DataProtection key ring — path is under a forbidden system directory ('{forbidden}'). " +
                        "Set LP_COMMON_KEYS_PATH to a user-writable location (e.g. /config/.lidarr-keys on Lidarr Docker images).");
                }
            }

            Directory.CreateDirectory(keysDir);

            var probe = Path.Combine(keysDir, $".lpc-write-probe-{Guid.NewGuid():N}");
            try
            {
                // FileShare.None prevents a concurrent process from racing
                // the probe; FileMode.CreateNew refuses if the name already
                // exists (defends against an adversary who guessed the GUID
                // a vanishingly small chance, but the symmetry is free).
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    stream.Write(ProbePayload, 0, ProbePayload.Length);
                    stream.Flush();

                    // Read back so write-then-truncate filesystems don't
                    // pass the probe falsely.
                    stream.Position = 0;
                    var roundTrip = new byte[ProbePayload.Length];
                    var read = stream.Read(roundTrip, 0, roundTrip.Length);
                    if (read != ProbePayload.Length)
                    {
                        throw new IOException($"Key-dir write probe failed: wrote {ProbePayload.Length} bytes, read back {read}.");
                    }
                    for (int i = 0; i < ProbePayload.Length; i++)
                    {
                        if (roundTrip[i] != ProbePayload[i])
                        {
                            throw new IOException("Key-dir write probe failed: payload corrupted during round-trip.");
                        }
                    }
                }
            }
            finally
            {
                try { File.Delete(probe); }
                catch
                {
                    // Best-effort cleanup. The probe is a tiny named file
                    // (random GUID); if delete fails (transient lock,
                    // immutable mount), the next call will simply create
                    // a fresh one with a different GUID. We can't safely
                    // signal the cleanup failure without breaking the happy
                    // path; trust that the actual DataProtection writes
                    // will succeed since the probe write+read did.
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
            var snapshot = new TokenProtectorDiagnostics(
                BackendName: backendName,
                KeysPath: keysPath,
                Cause: cause,
                DegradedReason: degradedReason);

            LastDiagnostics = snapshot;

            // Reflect the current call's outcome. A subsequent successful call
            // clears a prior degradation; a subsequent degraded call sets the
            // flag even if a prior call had succeeded.
            Volatile.Write(ref _degraded, degradedReason is null ? 0 : 1);

            // F8: fan-out event for subscribers that want to surface the
            // degradation beyond the static snapshot. Fires only on
            // degradation transitions, not on every successful call (which
            // would be noise).
            if (degradedReason is not null)
            {
                try
                {
                    DegradationDetected?.Invoke(snapshot);
                }
                catch
                {
                    // Swallow subscriber exceptions so a buggy log handler
                    // can't break the factory. Subscribers MUST be
                    // exception-safe per the docstring.
                }

                // Reset the one-shot warning latch so a recovery + re-degradation
                // can re-fire the warning on the next LogDegradationOnce call.
                Volatile.Write(ref _degradationWarningEmitted, 0);
            }
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
