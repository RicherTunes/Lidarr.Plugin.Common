using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Lidarr.Plugin.Common.Interop;

namespace Lidarr.Plugin.Common.Services.Authentication
{
    /// <summary>
    /// Persists token envelopes to disk using <see cref="System.Text.Json"/>.
    /// </summary>
    /// <typeparam name="TSession">Session representation type.</typeparam>
    internal sealed class FileTokenStore<TSession> : ITokenStore<TSession>
        where TSession : class
    {
        private readonly string _filePath;
        private readonly string _lockName;
        private readonly JsonSerializerOptions _serializerOptions;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ILogger<FileTokenStore<TSession>>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileTokenStore{TSession}"/> class.
        /// </summary>
        /// <param name="filePath">Absolute path used to persist the session.</param>
        /// <param name="serializerOptions">Optional serializer configuration for <typeparamref name="TSession"/>.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public FileTokenStore(string filePath, JsonSerializerOptions? serializerOptions = null, ILogger<FileTokenStore<TSession>>? logger = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must be supplied", nameof(filePath));
            }

            _filePath = Path.GetFullPath(filePath);
            _lockName = CreateLockName(_filePath);
            _logger = logger;
            _serializerOptions = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.General)
            {
                WriteIndented = true
            };

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        /// <inheritdoc />
        public async Task<TokenEnvelope<TSession>?> LoadAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var cp = AcquireCrossProcessLock(_lockName, cancellationToken);
                if (!File.Exists(_filePath))
                {
                    return null;
                }

                await using (var stream = new FileStream(_filePath, new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    Options = FileOptions.SequentialScan
                }))
                {
                    try
                    {
                        var persisted = await JsonSerializer.DeserializeAsync<PersistedEnvelope>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
                        if (persisted == null)
                        {
                            return null;
                        }

                        return new TokenEnvelope<TSession>(persisted.Session!, persisted.ExpiresAt, persisted.Metadata);
                    }
                    catch (JsonException) when (OperatingSystem.IsWindows())
                    {
                        // Fallback for DPAPI-protected payloads written on Windows
                        stream.Position = 0;
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                        var protectedBytes = ms.ToArray();
                        var plaintext = TryUnprotectWindows(protectedBytes);
                        if (plaintext == null)
                        {
                            return null;
                        }
                        var persisted = JsonSerializer.Deserialize<PersistedEnvelope>(plaintext, _serializerOptions);
                        if (persisted == null)
                        {
                            return null;
                        }
                        return new TokenEnvelope<TSession>(persisted.Session!, persisted.ExpiresAt, persisted.Metadata);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger?.LogWarning(ex, "Failed to load token envelope from {FilePath}", _filePath);
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task SaveAsync(TokenEnvelope<TSession> envelope, CancellationToken cancellationToken = default)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var cp = AcquireCrossProcessLock(_lockName, cancellationToken);
                var persisted = PersistedEnvelope.FromEnvelope(envelope);

                // Use a unique temp file name per save to avoid sharing violations
                var tempPath = _filePath + "." + Guid.NewGuid().ToString("n") + ".tmp";

                await using (var stream = new FileStream(tempPath, new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous
                }))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        // Protect persisted JSON using DPAPI under CurrentUser scope when available
                        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(persisted, _serializerOptions);
                        var protectedBytes = TryProtectWindows(jsonBytes) ?? jsonBytes;
                        await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await JsonSerializer.SerializeAsync(stream, persisted, _serializerOptions, cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                // Replace atomically when destination exists; otherwise a simple move is sufficient.
                // Add a small retry to mitigate transient Windows file locks (e.g., antivirus/indexer).
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(_filePath))
                        {
                            try
                            {
                                File.Replace(tempPath, _filePath, destinationBackupFileName: null);
                            }
                            catch (PlatformNotSupportedException)
                            {
                                File.Move(tempPath, _filePath, overwrite: true);
                            }
                        }
                        else
                        {
                            File.Move(tempPath, _filePath, overwrite: true);
                        }
                        // Harden file permissions on Unix-like systems
                        TryHardenFilePermissions(_filePath);
                        break; // success
                    }
                    catch (IOException) when (attempt < 10)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger?.LogError(ex, "Failed to save token envelope to {FilePath}", _filePath);
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task ClearAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var cp = AcquireCrossProcessLock(_lockName, cancellationToken);
                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                }
            }
            catch (Exception ex) when (ex is IOException)
            {
                _logger?.LogWarning(ex, "Failed to clear token envelope file {FilePath}", _filePath);
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string CreateLockName(string filePath)
        {
            // Stable name derived from full path; avoids leaking PII by hashing
            var hash = Lidarr.Plugin.Common.Utilities.HashingUtility.ComputeSHA256(filePath);
            return $"LPC.TokenStore.{hash.Substring(0, 32)}";
        }

        private static IDisposable AcquireCrossProcessLock(string name, CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows())
            {
                return new NamedMutexScope(name, cancellationToken);
            }
            else
            {
                return new FileLockScope(name, cancellationToken);
            }
        }

        private sealed class NamedMutexScope : IDisposable
        {
            private readonly Mutex _mutex;
            public NamedMutexScope(string name, CancellationToken cancellationToken)
            {
                _mutex = new Mutex(false, name);
                bool acquired = false;
                try
                {
                    // Allow a more generous cross-process wait to reduce
                    // rare false negatives on busy Windows CI runners.
                    acquired = _mutex.WaitOne(TimeSpan.FromSeconds(120));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true; // Consider abandoned as acquired to proceed
                }
                if (!acquired)
                {
                    throw new TimeoutException($"Failed to acquire cross-process token store mutex '{name}'.");
                }
                if (cancellationToken.CanBeCanceled)
                {
                    cancellationToken.Register(() =>
                    {
                        try { _mutex.ReleaseMutex(); } catch { }
                    });
                }
            }
            public void Dispose()
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
            }
        }

        private sealed class FileLockScope : IDisposable
        {
            private readonly FileStream _lockStream;
            public FileLockScope(string name, CancellationToken cancellationToken)
            {
                // Use a lock file in the OS temp directory; exclusive open provides cross-process coordination
                var tempDir = Path.GetTempPath();
                var lockPath = Path.Combine(tempDir, name + ".lock");
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        _lockStream = new FileStream(lockPath, new FileStreamOptions
                        {
                            Access = FileAccess.ReadWrite,
                            Mode = FileMode.OpenOrCreate,
                            Share = FileShare.None,
                            Options = FileOptions.DeleteOnClose
                        });
                        break; // acquired
                    }
                    catch (IOException)
                    {
                        if (DateTime.UtcNow >= deadline)
                        {
                            throw; // give up after timeout; surface original sharing violation
                        }
                        // Brief backoff before retrying; use a small sleep to avoid busy-wait
                        Thread.Sleep(50);
                    }
                }
            }
            public void Dispose()
            {
                try { _lockStream.Dispose(); } catch { }
            }
        }

        private sealed class PersistedEnvelope
        {
            public TSession? Session { get; set; }

            public DateTime? ExpiresAt { get; set; }

            public Dictionary<string, string>? Metadata { get; set; }

            public static PersistedEnvelope FromEnvelope(TokenEnvelope<TSession> envelope)
            {
                return new PersistedEnvelope
                {
                    Session = envelope.Session,
                    ExpiresAt = envelope.ExpiresAt,
                    Metadata = envelope.Metadata != null ? new Dictionary<string, string>(envelope.Metadata) : null
                };
            }
        }

        private static void TryHardenFilePermissions(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                try
                {
#if NET7_0_OR_GREATER
                    // 600: read/write for owner only
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#else
                    // Fallback for .NET 6: use chmod(2) when available
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        // 0o600
                        const uint S_IRUSR = 0x100; // 0400
                        const uint S_IWUSR = 0x80;  // 0200
                        _ = PosixInterop.Chmod(path, S_IRUSR | S_IWUSR);
                    }
#endif
                }
                catch
                {
                    // Best effort only; ignore if not supported
                }
            }
        }

        private byte[]? TryProtectWindows(byte[] plaintext)
        {
            try
            {
                var type = Type.GetType("System.Security.Cryptography.ProtectedData, System.Security.Cryptography.ProtectedData", throwOnError: false);
                if (type == null)
                {
                    return null;
                }
                var scopeType = Type.GetType("System.Security.Cryptography.DataProtectionScope, System.Security.Cryptography.ProtectedData", throwOnError: false);
                if (scopeType == null)
                {
                    return null;
                }
                var currentUser = Enum.ToObject(scopeType, 1);
                var method = type.GetMethod("Protect", BindingFlags.Public | BindingFlags.Static, new Type[] { typeof(byte[]), typeof(byte[]), scopeType });
                if (method == null)
                {
                    return null;
                }
                var result = method.Invoke(null, new object?[] { plaintext, null, currentUser }) as byte[];
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to protect token envelope with DPAPI");
                return null;
            }
        }

        private byte[]? TryUnprotectWindows(byte[] ciphertext)
        {
            try
            {
                var type = Type.GetType("System.Security.Cryptography.ProtectedData, System.Security.Cryptography.ProtectedData", throwOnError: false);
                if (type == null)
                {
                    return null;
                }
                var scopeType = Type.GetType("System.Security.Cryptography.DataProtectionScope, System.Security.Cryptography.ProtectedData", throwOnError: false);
                if (scopeType == null)
                {
                    return null;
                }
                var currentUser = Enum.ToObject(scopeType, 1);
                var method = type.GetMethod("Unprotect", BindingFlags.Public | BindingFlags.Static, new Type[] { typeof(byte[]), typeof(byte[]), scopeType });
                if (method == null)
                {
                    return null;
                }
                var result = method.Invoke(null, new object?[] { ciphertext, null, currentUser }) as byte[];
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to unprotect token envelope with DPAPI");
                return null;
            }
        }
    }
}
