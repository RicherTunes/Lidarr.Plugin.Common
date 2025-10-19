using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Security.TokenProtection;
using Microsoft.Extensions.Logging;

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
        private readonly ITokenProtector _protector;

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
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
            _protector = TokenProtectorFactory.CreateFromEnvironment();

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

                await using var stream = new FileStream(_filePath, new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    Options = FileOptions.SequentialScan
                });
                // Try protected format first
                var maybeProtected = await JsonSerializer.DeserializeAsync<ProtectedEnvelope>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
                if (maybeProtected != null && maybeProtected.V == 2 && !string.IsNullOrWhiteSpace(maybeProtected.Payload))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(maybeProtected.Payload);
                        var json = _protector.Unprotect(bytes);
                        var restored = JsonSerializer.Deserialize<PersistedEnvelope>(json, _serializerOptions);
                        if (restored == null) return null;
                        return new TokenEnvelope<TSession>(restored.Session!, restored.ExpiresAt, restored.Metadata);
                    }
                    catch (Exception ex) when (ex is FormatException or JsonException or IOException)
                    {
                        _logger?.LogWarning(ex, "Failed to decrypt token envelope from {FilePath}", _filePath);
                        return null;
                    }
                }

                // Legacy format fallback: rewind and parse legacy JSON then migrate in-place
                stream.Position = 0;
                var legacy = await JsonSerializer.DeserializeAsync<PersistedEnvelope>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
                if (legacy == null)
                {
                    return null;
                }

                TryMigrateToProtectedFormat(legacy);
                return new TokenEnvelope<TSession>(legacy.Session!, legacy.ExpiresAt, legacy.Metadata);
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
                var protectedEnv = CreateProtectedEnvelope(persisted);

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
                    await JsonSerializer.SerializeAsync(stream, protectedEnv, _serializerOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
                    acquired = _mutex.WaitOne(TimeSpan.FromSeconds(60));
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

        private sealed class ProtectedEnvelope
        {
            public int V { get; set; } = 2;
            public string Alg { get; set; } = string.Empty;
            public string Payload { get; set; } = string.Empty;
        }

        private ProtectedEnvelope CreateProtectedEnvelope(PersistedEnvelope payload)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(payload, _serializerOptions);
            var cipher = _protector.Protect(json);
            return new ProtectedEnvelope { V = 2, Alg = _protector.AlgorithmId, Payload = Convert.ToBase64String(cipher) };
        }

        private void TryMigrateToProtectedFormat(PersistedEnvelope legacy)
        {
            try
            {
                var protectedEnv = CreateProtectedEnvelope(legacy);
                var tempPath = _filePath + "." + Guid.NewGuid().ToString("n") + ".tmp";
                using (var stream = new FileStream(tempPath, new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous
                }))
                {
                    JsonSerializer.Serialize(stream, protectedEnv, _serializerOptions);
                    stream.Flush();
                }
                if (File.Exists(_filePath))
                {
                    try { File.Replace(tempPath, _filePath, destinationBackupFileName: null); } catch (PlatformNotSupportedException) { File.Move(tempPath, _filePath, overwrite: true); }
                }
                else
                {
                    File.Move(tempPath, _filePath, overwrite: true);
                }
#if NET8_0_OR_GREATER
                if (!OperatingSystem.IsWindows())
                {
                    try { File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
                }
#endif
            }
            catch
            {
                // best-effort migration: ignore failures
            }
        }
    }
}
