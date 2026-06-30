using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Collections
{
    /// <summary>
    /// A thread-safe dictionary with a hard capacity ceiling. When an insert would push
    /// the count past <see cref="Capacity"/>, the entire dictionary is cleared first and
    /// then the new entry is added ("clear-all on overflow" eviction). This matches the
    /// precedent established by Brainarr's MetricsCollector and LimiterRegistry as well as
    /// Qobuzarr's SharedSystemHttpClient — clear-all is simpler than LRU and the stale
    /// state that is lost reconstructs naturally on the next operation.
    /// </summary>
    /// <remarks>
    /// This class is NOT <see cref="IDisposable"/>; it owns no timers or other unmanaged
    /// resources. Thread-safety guarantees mirror those of <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </remarks>
    /// <typeparam name="TKey">The type of keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of values in the dictionary.</typeparam>
    public sealed class BoundedConcurrentDictionary<TKey, TValue>
        : IEnumerable<KeyValuePair<TKey, TValue>>
        where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, TValue> _inner;
        private readonly int _capacity;
        private readonly Action<int>? _onEvicted;
        private readonly object _evictLock = new();

        /// <summary>
        /// Initialises a new instance with the given capacity.
        /// </summary>
        /// <param name="capacity">Maximum number of entries. Must be greater than zero.</param>
        /// <param name="comparer">Optional key comparer. Defaults to the default comparer for <typeparamref name="TKey"/>.</param>
        /// <param name="onEvicted">
        /// Optional callback invoked when an eviction occurs. Receives the number of entries
        /// that were cleared. Useful for metrics/logging without coupling the type to a logger.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is zero or negative.</exception>
        public BoundedConcurrentDictionary(int capacity, IEqualityComparer<TKey>? comparer = null, Action<int>? onEvicted = null)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");

            _capacity = capacity;
            _onEvicted = onEvicted;
            _inner = comparer is null
                ? new ConcurrentDictionary<TKey, TValue>()
                : new ConcurrentDictionary<TKey, TValue>(comparer);
        }

        /// <summary>Gets the configured capacity ceiling.</summary>
        public int Capacity => _capacity;

        /// <summary>Gets the current number of entries. Snapshot — may change immediately.</summary>
        public int Count => _inner.Count;

        /// <summary>
        /// Returns a snapshot of all current keys.
        /// </summary>
        public ICollection<TKey> Keys => _inner.Keys;

        /// <summary>
        /// Returns a snapshot of all current values. Mirrors
        /// <see cref="ConcurrentDictionary{TKey, TValue}.Values"/>.
        /// </summary>
        public ICollection<TValue> Values => _inner.Values;

        /// <summary>
        /// Returns true if the dictionary contains the specified key. Mirrors
        /// <see cref="ConcurrentDictionary{TKey, TValue}.ContainsKey"/>; useful for "miss"
        /// caches whose values are byte sentinels and where TryGetValue is overkill.
        /// </summary>
        public bool ContainsKey(TKey key) => _inner.ContainsKey(key);

        /// <summary>
        /// Indexer that mirrors <see cref="ConcurrentDictionary{TKey, TValue}"/>'s.
        /// <para>Get: <see cref="KeyNotFoundException"/> when absent.</para>
        /// <para>Set: capacity-checked then assigns (overwrite semantics). Insert path runs
        /// <see cref="EvictIfNeeded"/> first so the indexer setter respects the cap.</para>
        /// </summary>
        public TValue this[TKey key]
        {
            get => _inner[key];
            set
            {
                lock (_evictLock)
                {
                    EvictIfNeededLocked();
                    _inner[key] = value;
                }
            }
        }

        /// <summary>
        /// Enumerates key/value pairs. Mirrors
        /// <see cref="ConcurrentDictionary{TKey, TValue}"/>'s snapshot-style enumerator —
        /// safe under concurrent mutation but does not observe writes that happen after
        /// enumeration begins.
        /// </summary>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Attempts to add the key/value pair. If the dictionary is at or above capacity,
        /// all existing entries are cleared before the new entry is inserted.
        /// </summary>
        /// <returns><c>true</c> if the key was added; <c>false</c> if it already existed.</returns>
        public bool TryAdd(TKey key, TValue value)
        {
            lock (_evictLock)
            {
                EvictIfNeededLocked();
                return _inner.TryAdd(key, value);
            }
        }

        /// <summary>
        /// Gets the value associated with the specified key.
        /// </summary>
        public bool TryGetValue(TKey key, out TValue value)
        {
#pragma warning disable CS8601 // ConcurrentDictionary<K,V>.TryGetValue out-param may be null on false.
            return _inner.TryGetValue(key, out value);
#pragma warning restore CS8601
        }

        /// <summary>
        /// Attempts to remove and return the value with the specified key.
        /// </summary>
        public bool TryRemove(TKey key, out TValue value)
        {
#pragma warning disable CS8601
            return _inner.TryRemove(key, out value);
#pragma warning restore CS8601
        }

        /// <summary>
        /// Adds a key/value pair or updates an existing key using the provided functions.
        /// If the dictionary is at or above capacity before the call, all entries are cleared first.
        /// </summary>
        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            lock (_evictLock)
            {
                EvictIfNeededLocked();
                return _inner.AddOrUpdate(key, addValue, updateValueFactory);
            }
        }

        /// <summary>
        /// Adds a key/value pair or updates an existing key using the provided factories.
        /// If the dictionary is at or above capacity before the call, all entries are cleared first.
        /// </summary>
        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
        {
            lock (_evictLock)
            {
                EvictIfNeededLocked();
                return _inner.AddOrUpdate(key, addValueFactory, updateValueFactory);
            }
        }

        /// <summary>
        /// Gets the value for a key, or adds it using the factory if absent.
        /// If the dictionary is at or above capacity before the call, all entries are cleared first.
        /// </summary>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            EvictIfNeeded();
            if (_inner.TryGetValue(key, out var existing))
                return existing;

            var value = valueFactory(key);

            lock (_evictLock)
            {
                EvictIfNeededLocked();
                if (_inner.TryGetValue(key, out existing))
                    return existing;

                _inner[key] = value;
                return value;
            }
        }

        /// <summary>
        /// Gets the value for a key, or adds the specified value if absent.
        /// If the dictionary is at or above capacity before the call, all entries are cleared first.
        /// </summary>
        public TValue GetOrAdd(TKey key, TValue value)
        {
            EvictIfNeeded();
            if (_inner.TryGetValue(key, out var existing))
                return existing;

            lock (_evictLock)
            {
                EvictIfNeededLocked();
                if (_inner.TryGetValue(key, out existing))
                    return existing;

                _inner[key] = value;
                return value;
            }
        }

        /// <summary>
        /// Removes all entries from the dictionary.
        /// </summary>
        public void Clear() => _inner.Clear();

        // ── internals ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clears all entries when the current count is at or above capacity, then fires
        /// the optional <see cref="_onEvicted"/> callback with the number of entries cleared.
        /// </summary>
        private void EvictIfNeeded()
        {
            if (_inner.Count < _capacity)
                return;

            lock (_evictLock)
            {
                EvictIfNeededLocked();
            }
        }

        private void EvictIfNeededLocked()
        {
            if (_inner.Count < _capacity)
                return;

            var cleared = _inner.Count;
            _inner.Clear();

            try { _onEvicted?.Invoke(cleared); } catch { /* never let a callback crash an insert */ }
        }
    }
}
