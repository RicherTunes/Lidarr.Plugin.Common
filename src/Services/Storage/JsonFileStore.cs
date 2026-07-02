// <copyright file="JsonFileStore.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Storage
{
    /// <summary>
    /// Generic JSON-backed key/value store with optional TTL and oldest-write size cap.
    /// Persists entries to a single file with atomic replace semantics, and serializes
    /// concurrent access through an in-process mutex. Suitable for plugin caches and
    /// small mapping stores (UPC -> MBID, ISO -> region, etc.).
    /// </summary>
    /// <typeparam name="TKey">Key type. Must serialize to a JSON-friendly string when <typeparamref name="TKey"/> is <see cref="string"/>; for other types, the default serializer rules apply.</typeparam>
    /// <typeparam name="TValue">Value type. Must be serializable by <see cref="System.Text.Json"/>.</typeparam>
    /// <remarks>
    /// <para>
    /// Atomic save: the store writes to a unique temp file then uses
    /// <see cref="File.Replace(string, string, string?)"/> (with <see cref="File.Move(string, string, bool)"/>
    /// fallback) to swap the file in. This matches the pattern used by
    /// <see cref="Authentication.FileTokenStore{TSession}"/> and prevents corruption from
    /// crashes during the write.
    /// </para>
    /// <para>
    /// TTL: when <see cref="JsonFileStoreOptions{TKey}.Ttl"/> is set, entries older than
    /// the TTL are treated as missing on read and quietly purged on the next save.
    /// </para>
    /// <para>
    /// Size cap: when <see cref="JsonFileStoreOptions{TKey}.MaxEntries"/> is set, the
    /// oldest entries (by write timestamp) are evicted on insert when the store
    /// would exceed the cap.
    /// </para>
    /// <para>
    /// Errors during load (corrupted JSON, IO failures) reset the in-memory state to empty
    /// and log a warning. Errors during save are logged; by default they do not throw,
    /// matching the behavior of similar stores that prefer "best-effort" persistence.
    /// Durable state machines can set <see cref="JsonFileStoreOptions{TKey}.ThrowOnSaveFailure"/>
    /// to propagate save failures and roll back the in-memory mutation.
    /// </para>
    /// </remarks>
    public sealed class JsonFileStore<TKey, TValue>
        where TKey : notnull
    {
        private readonly string _filePath;
        private readonly JsonSerializerOptions _options;
        private readonly TimeSpan? _ttl;
        private readonly int? _maxEntries;
        private readonly Func<TKey, TKey>? _keyNormalizer;
        private readonly IEqualityComparer<TKey>? _keyComparer;
        private readonly TimeProvider _clock;
        private readonly ILogger? _logger;
        private readonly bool _throwOnSaveFailure;
        private readonly SemaphoreSlim _mutex = new(1, 1);

        private Dictionary<TKey, Entry> _entries;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonFileStore{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="filePath">Absolute path of the JSON file used for persistence.</param>
        /// <param name="options">Optional store configuration (TTL, LRU cap, key normalization, comparer).</param>
        /// <param name="serializerOptions">Optional <see cref="JsonSerializerOptions"/>; defaults to camelCase, indented.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public JsonFileStore(
            string filePath,
            JsonFileStoreOptions<TKey>? options = null,
            JsonSerializerOptions? serializerOptions = null,
            ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must be supplied", nameof(filePath));
            }

            _filePath = Path.GetFullPath(filePath);
            options ??= new JsonFileStoreOptions<TKey>();
            _ttl = options.Ttl;
            _maxEntries = options.MaxEntries;
            _keyNormalizer = options.KeyNormalizer;
            _keyComparer = options.KeyComparer;
            _clock = options.Clock ?? TimeProvider.System;
            _throwOnSaveFailure = options.ThrowOnSaveFailure;
            _options = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
            };
            _logger = logger;

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _entries = LoadInitial();
        }

        /// <summary>
        /// Gets the current entry count (best-effort snapshot, includes expired entries
        /// until the next save purges them).
        /// </summary>
        public int Count
        {
            get
            {
                _mutex.Wait();
                try
                {
                    return _entries.Count;
                }
                finally
                {
                    _mutex.Release();
                }
            }
        }

        /// <summary>
        /// Retrieves a value by key. Returns the default value (typically null for reference
        /// types) when the key is not present or its entry has expired.
        /// </summary>
        /// <param name="key">Key to look up.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The stored value, or default when missing/expired.</returns>
        public async ValueTask<TValue?> GetAsync(TKey key, CancellationToken cancellationToken = default)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var normalized = NormalizeKey(key);
                if (_entries.TryGetValue(normalized, out var entry) && !IsExpired(entry))
                {
                    return entry.Value;
                }

                return default;
            }
            finally
            {
                _mutex.Release();
            }
        }

        /// <summary>
        /// Stores a value for the specified key. The entry's timestamp is updated to "now",
        /// which updates its write timestamp and protects it from immediate eviction.
        /// Triggers oldest-write eviction when <see cref="JsonFileStoreOptions{TKey}.MaxEntries"/> is set
        /// and the resulting size would exceed the cap.
        /// </summary>
        /// <param name="key">Key to set.</param>
        /// <param name="value">Value to store.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async ValueTask SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var rollback = _throwOnSaveFailure ? CloneEntries() : null;
                var normalized = NormalizeKey(key);
                try
                {
                    _entries[normalized] = new Entry { Value = value, Timestamp = _clock.GetUtcNow() };
                    EvictExpiredAndCap();
                    Save();
                }
                catch
                {
                    if (rollback is not null)
                    {
                        _entries = rollback;
                    }

                    throw;
                }
            }
            finally
            {
                _mutex.Release();
            }
        }

        /// <summary>
        /// Removes the entry with the specified key. Returns true if a matching entry existed.
        /// </summary>
        /// <param name="key">Key to remove.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if an entry was removed, false otherwise.</returns>
        public async ValueTask<bool> RemoveAsync(TKey key, CancellationToken cancellationToken = default)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var rollback = _throwOnSaveFailure ? CloneEntries() : null;
                var normalized = NormalizeKey(key);
                var removed = _entries.Remove(normalized);
                if (removed)
                {
                    try
                    {
                        Save();
                    }
                    catch
                    {
                        if (rollback is not null)
                        {
                            _entries = rollback;
                        }

                        throw;
                    }
                }

                return removed;
            }
            finally
            {
                _mutex.Release();
            }
        }

        /// <summary>
        /// Removes all entries from the store and persists the empty state.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var rollback = _throwOnSaveFailure ? CloneEntries() : null;
                _entries = new Dictionary<TKey, Entry>(_keyComparer);
                try
                {
                    Save();
                }
                catch
                {
                    if (rollback is not null)
                    {
                        _entries = rollback;
                    }

                    throw;
                }
            }
            finally
            {
                _mutex.Release();
            }
        }

        /// <summary>
        /// Enumerates non-expired entries ordered by timestamp ascending (oldest-first).
        /// Snapshot semantics: entries are copied under the lock so iteration does not block writers.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Async enumerable of key/value pairs.</returns>
        public async IAsyncEnumerable<KeyValuePair<TKey, TValue>> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<KeyValuePair<TKey, TValue>> snapshot;
            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                snapshot = _entries
                    .Where(kv => !IsExpired(kv.Value))
                    .OrderBy(kv => kv.Value.Timestamp)
                    .Select(kv => new KeyValuePair<TKey, TValue>(kv.Key, kv.Value.Value!))
                    .ToList();
            }
            finally
            {
                _mutex.Release();
            }

            foreach (var pair in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return pair;
            }
        }

        private TKey NormalizeKey(TKey key)
        {
            return _keyNormalizer != null ? _keyNormalizer(key) : key;
        }

        private Dictionary<TKey, Entry> CloneEntries()
        {
            var clone = new Dictionary<TKey, Entry>(_keyComparer);
            foreach (var pair in _entries)
            {
                clone[pair.Key] = new Entry
                {
                    Value = pair.Value.Value,
                    Timestamp = pair.Value.Timestamp,
                };
            }

            return clone;
        }

        private bool IsExpired(Entry entry)
        {
            return _ttl.HasValue && _clock.GetUtcNow() - entry.Timestamp > _ttl.Value;
        }

        private Dictionary<TKey, Entry> LoadInitial()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new Dictionary<TKey, Entry>(_keyComparer);
                }

                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new Dictionary<TKey, Entry>(_keyComparer);
                }

                var deserialized = JsonSerializer.Deserialize<Dictionary<TKey, Entry>>(json, _options);
                if (deserialized == null)
                {
                    return new Dictionary<TKey, Entry>(_keyComparer);
                }

                if (_keyComparer != null && !ReferenceEquals(deserialized.Comparer, _keyComparer))
                {
                    var rebuilt = new Dictionary<TKey, Entry>(_keyComparer);
                    foreach (var kv in deserialized)
                    {
                        rebuilt[kv.Key] = kv.Value;
                    }

                    return rebuilt;
                }

                return deserialized;
            }
            catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
            {
                _logger?.LogWarning(ex, "JsonFileStore failed to load {FilePath}; starting empty", _filePath);
                return new Dictionary<TKey, Entry>(_keyComparer);
            }
        }

        private void EvictExpiredAndCap()
        {
            if (_ttl.HasValue)
            {
                var expired = _entries
                    .Where(kv => IsExpired(kv.Value))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in expired)
                {
                    _entries.Remove(key);
                }
            }

            if (_maxEntries.HasValue && _entries.Count > _maxEntries.Value)
            {
                var excess = _entries.Count - _maxEntries.Value;
                var victims = _entries
                    .OrderBy(kv => kv.Value.Timestamp)
                    .Take(excess)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in victims)
                {
                    _entries.Remove(key);
                }
            }
        }

        private void Save()
        {
            try
            {
                var tempPath = _filePath + "." + Guid.NewGuid().ToString("n") + ".tmp";
                using (var stream = new FileStream(tempPath, new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough,
                }))
                {
                    JsonSerializer.Serialize(stream, _entries, _options);
                    stream.Flush();
                }

                // Atomic replace; tolerate platforms that don't support File.Replace.
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
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "JsonFileStore failed to save {FilePath}", _filePath);
                if (_throwOnSaveFailure)
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Internal entry record persisted alongside each value. Public so consumers can
        /// supply custom <see cref="JsonSerializerOptions"/> that round-trip the type when
        /// they need direct access to timestamps (e.g., diagnostics).
        /// </summary>
        public sealed class Entry
        {
            /// <summary>
            /// Gets or sets the stored value.
            /// </summary>
            public TValue? Value { get; set; }

            /// <summary>
            /// Gets or sets the UTC timestamp at which the entry was last set.
            /// Used for TTL expiry checks and oldest-write eviction ordering.
            /// </summary>
            public DateTimeOffset Timestamp { get; set; }
        }
    }

    /// <summary>
    /// Configuration for <see cref="JsonFileStore{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">Store key type.</typeparam>
    public sealed class JsonFileStoreOptions<TKey>
        where TKey : notnull
    {
        /// <summary>
        /// Gets or sets the optional time-to-live for entries. Entries whose timestamp is
        /// older than now - TTL are treated as missing on <see cref="JsonFileStore{TKey, TValue}.GetAsync"/>
        /// and purged on the next save. Null disables TTL (entries live forever).
        /// </summary>
        public TimeSpan? Ttl { get; set; }

        /// <summary>
        /// Gets or sets the optional maximum number of entries. When the store would exceed
        /// this count on insert, the oldest entries (by write timestamp) are evicted first.
        /// Null disables the cap.
        /// </summary>
        public int? MaxEntries { get; set; }

        /// <summary>
        /// Gets or sets an optional key normalization function applied at every API boundary
        /// (Get/Set/Remove). Useful for case-insensitive lookups, trimming whitespace, etc.
        /// </summary>
        public Func<TKey, TKey>? KeyNormalizer { get; set; }

        /// <summary>
        /// Gets or sets an optional equality comparer used by the underlying dictionary.
        /// For string keys, supply <see cref="StringComparer.OrdinalIgnoreCase"/> for case
        /// insensitive lookups. Null uses the default comparer.
        /// </summary>
        public IEqualityComparer<TKey>? KeyComparer { get; set; }

        /// <summary>
        /// Gets or sets the time source used for TTL timestamps and expiry checks. Defaults to
        /// <c>TimeProvider.System</c>. Tests can supply a fake provider to exercise TTL
        /// expiry deterministically without wall-clock <c>Task.Delay</c>.
        /// </summary>
        public TimeProvider? Clock { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether save failures are propagated to callers.
        /// Defaults to false for best-effort cache semantics. Durable state machines should
        /// set this to true so callers cannot report success for state that never reached disk.
        /// </summary>
        public bool ThrowOnSaveFailure { get; set; }
    }
}
