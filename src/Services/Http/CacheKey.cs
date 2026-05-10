using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Stable cache key for <see cref="CachingHttpExecutor"/>: the (endpoint, parameters) tuple that
    /// <see cref="Lidarr.Plugin.Common.Interfaces.IStreamingResponseCache"/> hashes into a backing storage key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plugins typically build a <see cref="CacheKey"/> from a request's canonical query — the same canonicalized
    /// form that <see cref="Lidarr.Plugin.Common.Utilities.QueryCanonicalizer"/> produces. The shape mirrors
    /// <see cref="Lidarr.Plugin.Common.Interfaces.IStreamingResponseCache.GenerateCacheKey(string, Dictionary{string, string})"/>
    /// so existing cache implementations work unchanged.
    /// </para>
    /// <para>
    /// <see cref="Endpoint"/> is the path portion (e.g., "/v1/catalog/us/albums/12345"), and
    /// <see cref="Parameters"/> carries any cache-relevant query parameters or scope tokens. Sensitive parameters
    /// should already be removed by the caller; the cache implementation also filters known-sensitive keys.
    /// </para>
    /// </remarks>
    public sealed class CacheKey : IEquatable<CacheKey>
    {
        /// <summary>Endpoint path (e.g., "/v1/catalog/us/albums").</summary>
        public string Endpoint { get; }

        /// <summary>
        /// Cache-relevant parameters. Returned as a fresh <see cref="Dictionary{TKey, TValue}"/> per call so
        /// the cache implementation can mutate it without affecting other callers.
        /// </summary>
        public Dictionary<string, string> Parameters
        {
            get
            {
                // Defensive copy: IStreamingResponseCache.GenerateCacheKey takes a Dictionary, and some
                // implementations buffer or mutate the input. Returning a fresh copy keeps CacheKey immutable.
                return new Dictionary<string, string>(_parameters, _parameters.Comparer);
            }
        }

        private readonly Dictionary<string, string> _parameters;

        /// <summary>
        /// Creates a new cache key for the given endpoint and parameters.
        /// </summary>
        /// <param name="endpoint">The endpoint path. Null is normalized to an empty string.</param>
        /// <param name="parameters">Cache-relevant parameters. Null is normalized to an empty dictionary.</param>
        public CacheKey(string? endpoint, IReadOnlyDictionary<string, string>? parameters = null)
        {
            Endpoint = endpoint ?? string.Empty;
            _parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    if (!string.IsNullOrEmpty(kv.Key))
                    {
                        _parameters[kv.Key] = kv.Value ?? string.Empty;
                    }
                }
            }
        }

        /// <summary>
        /// Convenience overload for <see cref="Dictionary{TKey, TValue}"/> input — avoids forcing callers to
        /// upcast to <see cref="IReadOnlyDictionary{TKey, TValue}"/>.
        /// </summary>
        public CacheKey(string? endpoint, Dictionary<string, string>? parameters)
            : this(endpoint, (IReadOnlyDictionary<string, string>?)parameters)
        {
        }

        /// <summary>
        /// Creates a parameter-free cache key for the given endpoint.
        /// </summary>
        public static CacheKey ForEndpoint(string endpoint) => new CacheKey(endpoint);

        /// <inheritdoc/>
        public bool Equals(CacheKey? other)
        {
            if (other is null) return false;
            if (!string.Equals(Endpoint, other.Endpoint, StringComparison.Ordinal)) return false;
            if (_parameters.Count != other._parameters.Count) return false;
            foreach (var kv in _parameters)
            {
                if (!other._parameters.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => Equals(obj as CacheKey);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            // Order-independent hash over (Endpoint, parameters)
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + Endpoint.GetHashCode(StringComparison.Ordinal);
                int paramHash = 0;
                foreach (var kv in _parameters)
                {
                    // XOR makes order independent; use combined per-pair hash so swapping key/value matters.
                    paramHash ^= HashCode.Combine(kv.Key, kv.Value);
                }
                hash = hash * 31 + paramHash;
                return hash;
            }
        }
    }
}
