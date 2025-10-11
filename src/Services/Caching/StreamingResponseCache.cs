using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using Lidarr.Plugin.Common.Interfaces;
using Microsoft.Extensions.Logging;
using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Services.Caching
{
    /// <summary>
    /// Generic response cache implementation for streaming service plugins.
    /// Uses an in-memory cache with TTL support.
    /// </summary>
    public abstract class StreamingResponseCache : IStreamingResponseCache
    {
        private readonly ConcurrentDictionary<string, CacheItem> _cache;
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _keysByEndpoint;
        private readonly object _cleanupLock = new object();
        private DateTime _lastCleanup = DateTime.UtcNow;
        private readonly ICachePolicyProvider? _policyProvider;
        private static readonly IReadOnlyDictionary<string, string> EmptyParameters = new Dictionary<string, string>();

        protected TimeSpan DefaultCacheDuration { get; set; } = TimeSpan.FromMinutes(15);
        protected int MaxCacheSize { get; set; } = 1000;
        protected TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
        protected Microsoft.Extensions.Logging.ILogger Logger { get; set; }

        protected StreamingResponseCache(Microsoft.Extensions.Logging.ILogger logger = null, ICachePolicyProvider? policyProvider = null)
        {
            _cache = new ConcurrentDictionary<string, CacheItem>();
            _keysByEndpoint = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);
            Logger = logger;
            _policyProvider = policyProvider;
        }

        /// <inheritdoc/>
        public T? Get<T>(string endpoint, Dictionary<string, string> parameters) where T : class
        {
            parameters ??= new Dictionary<string, string>();
            var policy = ResolvePolicy(endpoint, parameters);
            if (!policy.ShouldCache)
            {
                CleanupExpiredItems();
                return null;
            }

            var effectiveParams = ApplyPolicyParameters(parameters, policy);
            var cacheKey = GenerateCacheKey(endpoint, effectiveParams);

            if (_cache.TryGetValue(cacheKey, out var cacheItem))
            {
                var now = DateTime.UtcNow;
                if (cacheItem.ExpiresAt > now)
                {
                    // Sliding expiration support
                    if (policy.SlidingExpiration.HasValue && policy.SlidingExpiration.Value > TimeSpan.Zero)
                    {
                        var proposed = now.Add(policy.SlidingExpiration.Value);
                        DateTime? absoluteCap = null;
                        if (policy.AbsoluteExpiration.HasValue && policy.AbsoluteExpiration.Value > TimeSpan.Zero)
                        {
                            absoluteCap = cacheItem.CreatedAt.Add(policy.AbsoluteExpiration.Value);
                        }

                        var newExpiry = absoluteCap.HasValue ? (proposed < absoluteCap.Value ? proposed : absoluteCap.Value) : proposed;
                        var throttle = policy.SlidingRefreshWindow;
                        var canExtend = true;
                        if (throttle.HasValue && throttle.Value > TimeSpan.Zero)
                        {
                            var sinceLast = now - cacheItem.LastExtendedAt;
                            canExtend = sinceLast >= throttle.Value;
                        }

                        if (canExtend && newExpiry > cacheItem.ExpiresAt)
                        {
                            var old = cacheItem.ExpiresAt;
                            cacheItem.ExpiresAt = newExpiry;
                            cacheItem.LastExtendedAt = now;
                            OnSlidingExtended(endpoint, cacheKey, old, newExpiry);
                        }
                    }

                    OnCacheHit(endpoint, cacheKey);
                    return cacheItem.Value as T;
                }
                // Grace window: if the entry just expired, allow a short stale read to enable 304 revalidation flows.
                // This prevents races where the item expires between preflight cache check and conditional request.
                var staleGrace = TimeSpan.FromMilliseconds(200);
                if (cacheItem.ExpiresAt > now - staleGrace)
                {
                    OnCacheHit(endpoint, cacheKey);
                    return cacheItem.Value as T;
                }

                if (_cache.TryRemove(cacheKey, out var removedItem))
                {
                    RemoveKeyFromEndpointIndex(removedItem.Endpoint, cacheKey);
                }
                else
                {
                    RemoveKeyFromEndpointIndex(cacheItem.Endpoint, cacheKey);
                }
            }

            OnCacheMiss(endpoint, cacheKey);
            CleanupExpiredItems();
            return null;
        }

        /// <inheritdoc/>
        public void Set<T>(string endpoint, Dictionary<string, string> parameters, T value) where T : class
        {
            if (value == null)
            {
                return;
            }

            // Never cache HttpResponseMessage instances (they hold disposables/mutable state)
            if (value is System.Net.Http.HttpResponseMessage)
            {
                Logger?.LogWarning("Ignoring attempt to cache HttpResponseMessage for endpoint '{Endpoint}'. Cache only stores DTOs/value objects.", endpoint);
                return;
            }

            parameters ??= new Dictionary<string, string>();
            var policy = ResolvePolicy(endpoint, parameters);
            if (!policy.ShouldCache)
            {
                return;
            }

            SetInternal(endpoint, ApplyPolicyParameters(parameters, policy), value, policy.Duration, policy);
        }

        /// <inheritdoc/>
        public void Set<T>(string endpoint, Dictionary<string, string> parameters, T value, TimeSpan duration) where T : class
        {
            if (value == null)
            {
                return;
            }

            // Never cache HttpResponseMessage instances (they hold disposables/mutable state)
            if (value is System.Net.Http.HttpResponseMessage)
            {
                Logger?.LogWarning("Ignoring attempt to cache HttpResponseMessage for endpoint '{Endpoint}'. Cache only stores DTOs/value objects.", endpoint);
                return;
            }

            parameters ??= new Dictionary<string, string>();
            var policy = ResolvePolicy(endpoint, parameters);
            if (!policy.ShouldCache)
            {
                return;
            }

            SetInternal(endpoint, ApplyPolicyParameters(parameters, policy), value, duration, policy);
        }

        private void SetInternal<T>(string endpoint, Dictionary<string, string> parameters, T value, TimeSpan duration, CachePolicy policy) where T : class
        {
            var cacheKey = GenerateCacheKey(endpoint, parameters);
            var cacheSeed = BuildCacheKeySeed(endpoint, parameters);
            var normalizedEndpoint = NormalizeEndpointKey(endpoint);
            var createdAt = DateTime.UtcNow;
            var expiresAt = createdAt.Add(duration);
            if (policy.AbsoluteExpiration.HasValue)
            {
                var absolute = createdAt.Add(policy.AbsoluteExpiration.Value);
                if (absolute < expiresAt)
                {
                    expiresAt = absolute;
                }
            }

            if (expiresAt <= createdAt)
            {
                expiresAt = createdAt;
            }

            var appliedDuration = expiresAt - createdAt;

            var cacheItem = new CacheItem
            {
                Value = value,
                ExpiresAt = expiresAt,
                CreatedAt = createdAt,
                Endpoint = normalizedEndpoint,
                CacheKey = cacheKey,
                CacheKeySeed = cacheSeed,
                LastExtendedAt = createdAt
            };

            _cache.AddOrUpdate(cacheKey, cacheItem, (key, existing) =>
            {
                RemoveKeyFromEndpointIndex(existing.Endpoint, key);
                return cacheItem;
            });

            AddKeyToEndpointIndex(normalizedEndpoint, cacheKey);
            EnsureCacheWithinLimit();
            OnCacheSet(endpoint, cacheKey, appliedDuration);
        }

        protected virtual CachePolicy ResolvePolicy(string endpoint, Dictionary<string, string> parameters)
        {
            if (_policyProvider != null)
            {
                var effectiveParameters = parameters ?? new Dictionary<string, string>();
                return _policyProvider.GetPolicy(endpoint ?? string.Empty, effectiveParameters);
            }

            if (!ShouldCache(endpoint))
            {
                return CachePolicy.Disabled;
            }

            var duration = GetCacheDuration(endpoint);
            return CachePolicy.Default.With(duration: duration);
        }

        // Internal helper for utilities to query endpoint policy flags without exposing provider publicly.
        internal bool IsConditionalRevalidationEnabled(string endpoint, Dictionary<string, string> parameters)
        {
            try
            {
                var policy = ResolvePolicy(endpoint, parameters);
                return policy.EnableConditionalRevalidation && policy.ShouldCache;
            }
            catch
            {
                return false;
            }
        }


        /// <inheritdoc/>
        public virtual bool ShouldCache(string endpoint)
        {
            if (_policyProvider != null)
            {
                return _policyProvider.GetPolicy(endpoint ?? string.Empty, EmptyParameters).ShouldCache;
            }

            return true;
        }

        /// <inheritdoc/>
        public virtual TimeSpan GetCacheDuration(string endpoint)
        {
            if (_policyProvider != null)
            {
                return _policyProvider.GetPolicy(endpoint ?? string.Empty, EmptyParameters).Duration;
            }

            return DefaultCacheDuration;
        }

        /// <inheritdoc/>
        public virtual string GenerateCacheKey(string endpoint, Dictionary<string, string> parameters)
        {
            var seed = BuildCacheKeySeed(endpoint, parameters);
            return HashCacheKeySeed(seed);
        }

        /// <inheritdoc/>
        public void Clear()
        {
            _cache.Clear();
            _keysByEndpoint.Clear();
            OnCacheCleared();
        }

        /// <inheritdoc/>
        public virtual void ClearEndpoint(string endpoint)
        {
            var normalizedEndpoint = NormalizeEndpointKey(endpoint);
            var callbackEndpoint = string.IsNullOrWhiteSpace(endpoint) ? normalizedEndpoint : endpoint;

            if (_keysByEndpoint.TryRemove(normalizedEndpoint, out var bucket))
            {
                var removed = 0;
                foreach (var key in bucket.Keys)
                {
                    if (_cache.TryRemove(key, out _))
                    {
                        removed++;
                    }
                }

                OnEndpointCleared(callbackEndpoint, removed);
            }
            else
            {
                OnEndpointCleared(callbackEndpoint, 0);
            }
        }

        /// <summary>
        /// Gets the service name for cache key prefixing.
        /// </summary>
        protected abstract string GetServiceName();

        /// <summary>
        /// Called when cache hit occurs. Override for custom handling.
        /// </summary>
        protected virtual void OnCacheHit(string cacheKey) { }

        /// <summary>
        /// Called when cache miss occurs. Override for custom handling.
        /// </summary>
        protected virtual void OnCacheMiss(string cacheKey) { }

        /// <summary>
        /// Called when cache eviction occurs. Override for custom handling.
        /// </summary>
        protected virtual void OnCacheEviction(string cacheKey, TimeSpan age) { }

        /// <summary>
        /// Called when an endpoint is cleared. Override for custom handling.
        /// </summary>
        protected virtual void OnEndpointCleared(string endpoint, int itemsRemoved) { }

        /// <summary>
        /// Override to filter sensitive parameters from cache keys.
        /// </summary>
        protected virtual bool ShouldFilterParameter(string parameterName, object parameterValue)
        {
            return IsSensitiveParameter(parameterName);
        }

        /// <summary>
        /// Get cache statistics. Override for custom statistics.
        /// </summary>
        protected virtual object GetStatistics()
        {
            return new
            {
                TotalEntries = _cache.Count,
                HitRatio = 0.0,
                TotalHits = 0L,
                TotalMisses = 0L,
                MemoryUsageEstimate = 0L,
                OldestEntryAge = TimeSpan.Zero
            };
        }

        /// <summary>
        /// Invalidate entries by prefix. Override for custom logic.
        /// </summary>
        protected virtual void InvalidateByPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return;
            }

            var keysToRemove = _cache
                .Where(kvp => MatchesPrefix(kvp.Key, kvp.Value, prefix))
                .Select(kvp => (kvp.Key, kvp.Value.Endpoint))
                .ToList();

            foreach (var entry in keysToRemove)
            {
                if (_cache.TryRemove(entry.Key, out var removed))
                {
                    RemoveKeyFromEndpointIndex(removed.Endpoint, entry.Key);
                }
                else
                {
                    RemoveKeyFromEndpointIndex(entry.Endpoint, entry.Key);
                }
            }
        }

        /// <summary>
        /// Count entries by prefix. Override for custom logic.
        /// </summary>
        protected virtual int CountEntriesByPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return 0;
            }

            return _cache.Count(kvp => MatchesPrefix(kvp.Key, kvp.Value, prefix));
        }

        /// <summary>
        /// Determines if a parameter is sensitive and should be excluded from cache keys.
        /// </summary>
        protected virtual bool IsSensitiveParameter(string parameterName)
        {
            return SensitiveKeys.IsSensitive(parameterName);
        }
        /// <summary>
        /// Called when a cache hit occurs.
        /// </summary>
        protected virtual void OnCacheHit(string endpoint, string cacheKey)
        {
            try { Observability.Metrics.CacheHit.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint)); } catch { }
            OnCacheHit(cacheKey);
        }

        /// <summary>
        /// Called when a cache miss occurs.
        /// </summary>
        protected virtual void OnCacheMiss(string endpoint, string cacheKey)
        {
            try { Observability.Metrics.CacheMiss.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint)); } catch { }
            OnCacheMiss(cacheKey);
        }

        /// <summary>
        /// Called when an item is set in the cache.
        /// </summary>
        protected virtual void OnCacheSet(string endpoint, string cacheKey, TimeSpan duration) { }

        /// <summary>
        /// Called when the entire cache is cleared.
        /// </summary>
        protected virtual void OnCacheCleared() { }

        private void EnsureCacheWithinLimit()
        {
            if (MaxCacheSize <= 0)
            {
                return;
            }

            var overage = _cache.Count - MaxCacheSize;
            if (overage <= 0)
            {
                return;
            }

            var victims = _cache
                .OrderBy(kvp => kvp.Value.CreatedAt)
                .Take(overage)
                .ToList();

            foreach (var victim in victims)
            {
                if (_cache.TryRemove(victim.Key, out var removed))
                {
                    RemoveKeyFromEndpointIndex(removed.Endpoint, victim.Key);
                }
                else if (_cache.TryGetValue(victim.Key, out var fallback))
                {
                    RemoveKeyFromEndpointIndex(fallback.Endpoint, victim.Key);
                }
            }
        }

        private void CleanupExpiredItems()
        {
            // Only cleanup every configured interval to avoid performance impact
            if (DateTime.UtcNow - _lastCleanup < CleanupInterval)
                return;

            lock (_cleanupLock)
            {
                if (DateTime.UtcNow - _lastCleanup < CleanupInterval)
                    return;

                var now = DateTime.UtcNow;
                var expiredEntries = _cache
                    .Where(kvp => kvp.Value.ExpiresAt <= now)
                    .Select(kvp => new { kvp.Key, kvp.Value })
                    .ToList();

                foreach (var entry in expiredEntries)
                {
                    if (_cache.TryRemove(entry.Key, out var removed))
                    {
                        RemoveKeyFromEndpointIndex(removed.Endpoint, entry.Key);
                    }
                    else
                    {
                        RemoveKeyFromEndpointIndex(entry.Value.Endpoint, entry.Key);
                    }
                }

                _lastCleanup = now;

                if (expiredEntries.Count > 0)
                {
                    OnExpiredItemsCleanup(expiredEntries.Count);
                }
            }
        }

        /// <summary>
        /// Called when expired items are cleaned up.
        /// </summary>
        protected virtual void OnExpiredItemsCleanup(int itemsRemoved) { }

        private string NormalizeEndpointKey(string endpoint)
        {
            return string.IsNullOrWhiteSpace(endpoint) ? string.Empty : endpoint.Trim();
        }

        protected virtual string BuildCacheKeySeed(string endpoint, Dictionary<string, string> parameters)
        {
            endpoint = NormalizeEndpointKey(endpoint);
            parameters ??= new Dictionary<string, string>();

            // Prefer canonical parameter string if provided by the request builder via Options
            string? canonicalParamString = null;
            try
            {
                if (parameters.TryGetValue(Lidarr.Plugin.Common.Services.Http.PluginHttpOptions.ParametersKey.Key, out var c) && !string.IsNullOrEmpty(c))
                {
                    canonicalParamString = c;
                }
            }
            catch { }

            string[] relevantParams;
            if (!string.IsNullOrEmpty(canonicalParamString))
            {
                relevantParams = new[] { canonicalParamString };
            }
            else
            {
                relevantParams = parameters
                    .Where(p => !IsSensitiveParameter(p.Key))
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => $"{p.Key}={p.Value}")
                    .ToArray();
            }

            // If callers supply a non-PII scope in parameters (e.g., "scope"), include it in the seed when present in effective parameters.
            string scopeComponent = string.Empty;
            if (parameters.TryGetValue("scope", out var scopeVal) && !string.IsNullOrWhiteSpace(scopeVal))
            {
                scopeComponent = "|scope:" + scopeVal;
            }

            var paramString = string.Join("&", relevantParams);
            return $"{GetServiceName()}|{endpoint}|{paramString}{scopeComponent}";
        }

        protected virtual string HashCacheKeySeed(string seed)
        {
            return HashingUtility.ComputeSHA256(seed);
        }

        private void AddKeyToEndpointIndex(string endpoint, string cacheKey)
        {
            endpoint = NormalizeEndpointKey(endpoint);
            if (string.IsNullOrEmpty(cacheKey))
            {
                return;
            }

            var bucket = _keysByEndpoint.GetOrAdd(endpoint, _ => new ConcurrentDictionary<string, byte>());
            bucket[cacheKey] = 0;
        }

        private void RemoveKeyFromEndpointIndex(string endpoint, string cacheKey)
        {
            endpoint = NormalizeEndpointKey(endpoint);

            if (!_keysByEndpoint.TryGetValue(endpoint, out var bucket))
            {
                return;
            }

            bucket.TryRemove(cacheKey, out _);

            if (bucket.IsEmpty)
            {
                _keysByEndpoint.TryRemove(endpoint, out _);
            }
        }

        private static bool MatchesPrefix(string cacheKey, CacheItem cacheItem, string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(cacheItem.CacheKeySeed) &&
                cacheItem.CacheKeySeed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return cacheKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private class CacheItem
        {
            public object Value { get; set; }
            public DateTime ExpiresAt { get; set; }
            public DateTime CreatedAt { get; set; }
            public string Endpoint { get; set; } = string.Empty;
            public string CacheKey { get; set; } = string.Empty;
            public string CacheKeySeed { get; set; } = string.Empty;
            public DateTime LastExtendedAt { get; set; }
        }

        private static Dictionary<string, string> ApplyPolicyParameters(Dictionary<string, string> parameters, CachePolicy policy)
        {
            // Filter scope unless policy opts into varying by it
            if (!policy.VaryByScope && parameters.ContainsKey("scope"))
            {
                var copy = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
                copy.Remove("scope");
                return copy;
            }
            return parameters;
        }

        /// <summary>
        /// Called when a sliding expiration extension occurs. Allows tests/observers to count coalesced updates.
        /// </summary>
        protected virtual void OnSlidingExtended(string endpoint, string cacheKey, DateTime previousExpiry, DateTime newExpiry) { }
    }
}
