// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Caching
{
    /// <summary>
    /// Cache entry priority levels that affect TTL and eviction behavior.
    /// </summary>
    public enum CacheEntryPriority
    {
        /// <summary>Low priority - short TTL, first to evict (e.g., search results).</summary>
        Low,

        /// <summary>Normal priority - medium TTL (e.g., standard API responses).</summary>
        Normal,

        /// <summary>High priority - long TTL, last to evict (e.g., metadata that rarely changes).</summary>
        High
    }

    /// <summary>
    /// Configuration options for SmartCache behavior.
    /// </summary>
    public sealed class SmartCacheOptions
    {
        /// <summary>Maximum number of entries before eviction triggers.</summary>
        public int MaxCacheSize { get; set; } = 10000;

        /// <summary>Number of entries to evict when max size is reached.</summary>
        public int EvictionBatchSize { get; set; } = 1000;

        /// <summary>Default expiry for entries without explicit priority.</summary>
        public TimeSpan DefaultExpiry { get; set; } = TimeSpan.FromHours(24);

        /// <summary>Expiry for low priority entries.</summary>
        public TimeSpan LowPriorityExpiry { get; set; } = TimeSpan.FromHours(6);

        /// <summary>Expiry for normal priority entries.</summary>
        public TimeSpan NormalPriorityExpiry { get; set; } = TimeSpan.FromDays(1);

        /// <summary>Expiry for high priority entries.</summary>
        public TimeSpan HighPriorityExpiry { get; set; } = TimeSpan.FromDays(3);

        /// <summary>Expiry for frequently accessed entries (popular items).</summary>
        public TimeSpan PopularItemExpiry { get; set; } = TimeSpan.FromDays(7);

        /// <summary>Minimum access count to be considered popular.</summary>
        public int PopularityThreshold { get; set; } = 10;

        /// <summary>Default options with sensible defaults.</summary>
        public static SmartCacheOptions Default => new SmartCacheOptions();

        /// <summary>Options optimized for high-throughput scenarios.</summary>
        public static SmartCacheOptions HighThroughput => new SmartCacheOptions
        {
            MaxCacheSize = 50000,
            EvictionBatchSize = 5000,
            LowPriorityExpiry = TimeSpan.FromHours(1),
            NormalPriorityExpiry = TimeSpan.FromHours(6),
            HighPriorityExpiry = TimeSpan.FromDays(1),
            PopularItemExpiry = TimeSpan.FromDays(3)
        };

        /// <summary>Options optimized for memory-constrained environments.</summary>
        public static SmartCacheOptions LowMemory => new SmartCacheOptions
        {
            MaxCacheSize = 1000,
            EvictionBatchSize = 100,
            LowPriorityExpiry = TimeSpan.FromMinutes(30),
            NormalPriorityExpiry = TimeSpan.FromHours(2),
            HighPriorityExpiry = TimeSpan.FromHours(12),
            PopularItemExpiry = TimeSpan.FromDays(1)
        };
    }

    /// <summary>
    /// Statistics about cache performance.
    /// </summary>
    public sealed class SmartCacheStatistics
    {
        /// <summary>Total number of cache lookups.</summary>
        public long TotalQueries { get; set; }

        /// <summary>Number of cache hits.</summary>
        public long CacheHits { get; set; }

        /// <summary>Number of cache misses.</summary>
        public long CacheMisses { get; set; }

        /// <summary>Cache hit rate (0.0 to 1.0).</summary>
        public double HitRate { get; set; }

        /// <summary>Current number of entries in the cache.</summary>
        public int CurrentSize { get; set; }

        /// <summary>Maximum allowed cache size.</summary>
        public int MaxSize { get; set; }

        /// <summary>Total number of entries evicted.</summary>
        public long Evictions { get; set; }

        /// <summary>Number of unique access patterns tracked.</summary>
        public int UniquePatterns { get; set; }

        /// <summary>Number of entries marked as popular.</summary>
        public int PopularEntries { get; set; }
    }

    /// <summary>
    /// Intelligent cache with ML-inspired eviction strategies and pattern learning.
    /// Provides LFU-LRU hybrid eviction, priority-based TTL, and access pattern tracking.
    /// </summary>
    /// <remarks>
    /// This implementation is inspired by Qobuzarr's SmartQueryCache and generalized
    /// for use across all plugins. Key features:
    /// - LFU-LRU hybrid eviction (balances frequency and recency)
    /// - Priority-based TTL calculation
    /// - Automatic popularity detection
    /// - Thread-safe concurrent operations
    /// - Comprehensive statistics tracking
    /// </remarks>
    /// <typeparam name="TKey">The type of cache keys.</typeparam>
    /// <typeparam name="TValue">The type of cached values.</typeparam>
    public sealed class SmartCache<TKey, TValue> : IDisposable
        where TKey : notnull
    {
        private readonly ILogger? _logger;
        private readonly SmartCacheOptions _options;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache;
        private readonly ConcurrentDictionary<string, AccessPattern> _patterns;
        private readonly Func<TKey, string> _keySerializer;
        private readonly object _evictionLock = new object();

        // Performance metrics
        private long _hits;
        private long _misses;
        private long _evictions;

        /// <summary>
        /// Initializes a new instance of the SmartCache class.
        /// </summary>
        /// <param name="keySerializer">Function to convert keys to string for hashing.</param>
        /// <param name="options">Cache configuration options.</param>
        /// <param name="logger">Optional logger for diagnostic output.</param>
        public SmartCache(
            Func<TKey, string> keySerializer,
            SmartCacheOptions? options = null,
            ILogger? logger = null)
        {
            _keySerializer = keySerializer ?? throw new ArgumentNullException(nameof(keySerializer));
            _options = options ?? SmartCacheOptions.Default;
            _logger = logger;
            _cache = new ConcurrentDictionary<string, CacheEntry>();
            _patterns = new ConcurrentDictionary<string, AccessPattern>();

            _logger?.LogDebug("SmartCache initialized with max size: {MaxSize}", _options.MaxCacheSize);
        }

        /// <summary>
        /// Attempts to get a cached value.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="value">The cached value if found.</param>
        /// <returns>True if the value was found and not expired; otherwise, false.</returns>
        public bool TryGet(TKey key, out TValue? value)
        {
            var cacheKey = GenerateCacheKey(key);

            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                if (!entry.IsExpired)
                {
                    Interlocked.Increment(ref _hits);
                    entry.RecordAccess();
                    value = entry.Data;
                    return true;
                }
                else
                {
                    // Remove expired entry
                    _cache.TryRemove(cacheKey, out _);
                }
            }

            Interlocked.Increment(ref _misses);
            value = default;
            return false;
        }

        /// <summary>
        /// Stores a value in the cache with automatic TTL based on priority.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="value">The value to cache.</param>
        /// <param name="priority">The priority level affecting TTL and eviction.</param>
        /// <param name="patternKey">Optional pattern key for tracking access patterns.</param>
        public void Set(TKey key, TValue value, CacheEntryPriority priority = CacheEntryPriority.Normal, string? patternKey = null)
        {
            var cacheKey = GenerateCacheKey(key);
            var expiry = CalculateExpiry(patternKey, priority);

            var entry = new CacheEntry
            {
                Key = cacheKey,
                Data = value,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(expiry),
                Priority = priority,
                LastAccessed = DateTime.UtcNow
            };

            _cache.AddOrUpdate(cacheKey, entry, (k, old) => entry);

            // Record pattern for future predictions
            if (!string.IsNullOrEmpty(patternKey))
            {
                RecordAccessPattern(patternKey, priority);
            }

            // Trigger eviction if needed (with synchronization)
            if (_cache.Count > _options.MaxCacheSize)
            {
                lock (_evictionLock)
                {
                    // Double-check after acquiring lock
                    if (_cache.Count > _options.MaxCacheSize)
                    {
                        EvictLeastValuable();
                    }
                }
            }
        }

        /// <summary>
        /// Stores a value in the cache with explicit TTL.
        /// </summary>
        /// <param name="key">The cache key.</param>
        /// <param name="value">The value to cache.</param>
        /// <param name="ttl">The time-to-live for this entry.</param>
        public void Set(TKey key, TValue value, TimeSpan ttl)
        {
            var cacheKey = GenerateCacheKey(key);

            var entry = new CacheEntry
            {
                Key = cacheKey,
                Data = value,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(ttl),
                Priority = CacheEntryPriority.Normal,
                LastAccessed = DateTime.UtcNow
            };

            _cache.AddOrUpdate(cacheKey, entry, (k, old) => entry);

            // Trigger eviction if needed
            if (_cache.Count > _options.MaxCacheSize)
            {
                lock (_evictionLock)
                {
                    if (_cache.Count > _options.MaxCacheSize)
                    {
                        EvictLeastValuable();
                    }
                }
            }
        }

        /// <summary>
        /// Removes a specific entry from the cache.
        /// </summary>
        /// <param name="key">The cache key to remove.</param>
        /// <returns>True if the entry was removed; otherwise, false.</returns>
        public bool Remove(TKey key)
        {
            var cacheKey = GenerateCacheKey(key);
            return _cache.TryRemove(cacheKey, out _);
        }

        /// <summary>
        /// Clears all entries from the cache.
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
            _patterns.Clear();
            Interlocked.Exchange(ref _hits, 0);
            Interlocked.Exchange(ref _misses, 0);
            Interlocked.Exchange(ref _evictions, 0);
            _logger?.LogDebug("SmartCache cleared");
        }

        /// <summary>
        /// Gets current cache performance statistics.
        /// </summary>
        public SmartCacheStatistics GetStatistics()
        {
            var total = Interlocked.Read(ref _hits) + Interlocked.Read(ref _misses);
            var hits = Interlocked.Read(ref _hits);
            var popularCount = _patterns.Values.Count(p => p.AccessFrequency >= _options.PopularityThreshold);

            return new SmartCacheStatistics
            {
                TotalQueries = total,
                CacheHits = hits,
                CacheMisses = Interlocked.Read(ref _misses),
                HitRate = total > 0 ? (double)hits / total : 0,
                CurrentSize = _cache.Count,
                MaxSize = _options.MaxCacheSize,
                Evictions = Interlocked.Read(ref _evictions),
                UniquePatterns = _patterns.Count,
                PopularEntries = popularCount
            };
        }

        /// <summary>
        /// Checks if a pattern is considered popular based on access frequency.
        /// </summary>
        /// <param name="patternKey">The pattern key to check.</param>
        /// <returns>True if the pattern exceeds the popularity threshold.</returns>
        public bool IsPopularPattern(string patternKey)
        {
            if (string.IsNullOrEmpty(patternKey))
            {
                return false;
            }

            return _patterns.TryGetValue(patternKey.ToLowerInvariant(), out var pattern)
                && pattern.AccessFrequency >= _options.PopularityThreshold;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _cache.Clear();
            _patterns.Clear();
        }

        #region Private Methods

        private string GenerateCacheKey(TKey key)
        {
            var input = _keySerializer(key);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToBase64String(hash).Substring(0, 16);
        }

        private TimeSpan CalculateExpiry(string? patternKey, CacheEntryPriority priority)
        {
            // Check if this is a popular pattern
            if (!string.IsNullOrEmpty(patternKey) && IsPopularPattern(patternKey))
            {
                return _options.PopularItemExpiry;
            }

            return priority switch
            {
                CacheEntryPriority.Low => _options.LowPriorityExpiry,
                CacheEntryPriority.Normal => _options.NormalPriorityExpiry,
                CacheEntryPriority.High => _options.HighPriorityExpiry,
                _ => _options.DefaultExpiry
            };
        }

        private void RecordAccessPattern(string patternKey, CacheEntryPriority priority)
        {
            var normalizedKey = patternKey.ToLowerInvariant();

            _patterns.AddOrUpdate(
                normalizedKey,
                new AccessPattern
                {
                    Key = normalizedKey,
                    Priority = priority,
                    FirstSeen = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                    AccessFrequency = 1
                },
                (k, existing) =>
                {
                    existing.AccessFrequency++;
                    existing.LastSeen = DateTime.UtcNow;
                    return existing;
                });
        }

        private void EvictLeastValuable()
        {
            var candidates = _cache.Values
                .OrderBy(e => CalculateEvictionScore(e))
                .Take(_options.EvictionBatchSize)
                .ToList();

            foreach (var entry in candidates)
            {
                if (_cache.TryRemove(entry.Key, out _))
                {
                    Interlocked.Increment(ref _evictions);
                }
            }

            _logger?.LogDebug("Evicted {Count} cache entries, current size: {Size}", candidates.Count, _cache.Count);
        }

        /// <summary>
        /// Calculates eviction score using LFU-LRU hybrid algorithm.
        /// Lower score = more likely to be evicted.
        /// </summary>
        private double CalculateEvictionScore(CacheEntry entry)
        {
            var age = (DateTime.UtcNow - entry.CreatedAt).TotalHours;
            var recency = (DateTime.UtcNow - entry.LastAccessed).TotalHours;
            var frequency = entry.AccessCount;

            // LFU-LRU hybrid: balance frequency and recency
            var score = (frequency * 10.0) / (recency + 1.0);

            // Boost score based on priority
            score *= entry.Priority switch
            {
                CacheEntryPriority.High => 3.0,
                CacheEntryPriority.Normal => 1.5,
                CacheEntryPriority.Low => 1.0,
                _ => 1.0
            };

            // Penalize very old entries
            if (age > 72)
            {
                score *= 0.5;
            }

            return score;
        }

        #endregion

        #region Internal Classes

        private sealed class CacheEntry
        {
            public string Key { get; set; } = string.Empty;
            public TValue? Data { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime ExpiresAt { get; set; }
            public DateTime LastAccessed { get; set; }
            public CacheEntryPriority Priority { get; set; }
            private int _accessCount;
            public int AccessCount => _accessCount;

            public bool IsExpired => DateTime.UtcNow > ExpiresAt;

            public void RecordAccess()
            {
                LastAccessed = DateTime.UtcNow;
                Interlocked.Increment(ref _accessCount);
            }
        }

        private sealed class AccessPattern
        {
            public string Key { get; set; } = string.Empty;
            public CacheEntryPriority Priority { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public int AccessFrequency { get; set; }
        }

        #endregion
    }
}
