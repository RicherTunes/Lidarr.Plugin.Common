using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Authentication
{
    /// <summary>
    /// Persists token envelopes to disk using <see cref="System.Text.Json"/>.
    /// </summary>
    /// <typeparam name="TSession">Session representation type.</typeparam>
    public sealed class FileTokenStore<TSession> : ITokenStore<TSession>
        where TSession : class
    {
        private readonly string _filePath;
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

                var persisted = await JsonSerializer.DeserializeAsync<PersistedEnvelope>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
                if (persisted == null)
                {
                    return null;
                }

                return new TokenEnvelope<TSession>(persisted.Session!, persisted.ExpiresAt, persisted.Metadata);
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
                var persisted = PersistedEnvelope.FromEnvelope(envelope);
                var tempPath = _filePath + ".tmp";

                await using (var stream = new FileStream(tempPath, new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.Create,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous
                }))
                {
                    await JsonSerializer.SerializeAsync(stream, persisted, _serializerOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, _filePath, true);
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
    }
}
