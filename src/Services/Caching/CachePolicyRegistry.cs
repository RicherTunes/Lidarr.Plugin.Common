using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Services.Caching
{
    /// <summary>
    /// Registry that maps endpoints (and optional predicates) to cache policies.
    /// Later registrations override earlier ones.
    /// </summary>
    internal sealed class CachePolicyRegistry : ICachePolicyProvider
    {
        private readonly List<CachePolicyRule> _rules = new List<CachePolicyRule>();
        private CachePolicy _defaultPolicy;

        private static readonly IReadOnlyDictionary<string, string> EmptyParameters = new Dictionary<string, string>();

        public CachePolicyRegistry(CachePolicy? defaultPolicy = null)
        {
            _defaultPolicy = defaultPolicy ?? CachePolicy.Default;
        }

        public CachePolicyRegistry SetDefault(CachePolicy policy)
        {
            _defaultPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
            return this;
        }

        public CachePolicyRegistry AddEndpoint(string endpoint, CachePolicy policy)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be null or whitespace.", nameof(endpoint));
            }

            return AddPredicate(
                (ep, _) => string.Equals(ep, endpoint, StringComparison.OrdinalIgnoreCase),
                policy);
        }

        public CachePolicyRegistry AddPrefix(string prefix, CachePolicy policy)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                throw new ArgumentException("Prefix cannot be null or whitespace.", nameof(prefix));
            }

            return AddPredicate(
                (ep, _) => ep.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                policy);
        }

        public CachePolicyRegistry AddPredicate(
            Func<string, IReadOnlyDictionary<string, string>, bool> predicate,
            CachePolicy policy)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            _rules.Add(new CachePolicyRule(predicate, policy));
            return this;
        }

        public CachePolicy GetPolicy(string endpoint, IReadOnlyDictionary<string, string> parameters)
        {
            var effectiveEndpoint = endpoint ?? string.Empty;
            var effectiveParameters = parameters ?? EmptyParameters;

            for (var i = _rules.Count - 1; i >= 0; i--)
            {
                var rule = _rules[i];
                if (rule.Predicate(effectiveEndpoint, effectiveParameters))
                {
                    return rule.Policy;
                }
            }

            return _defaultPolicy;
        }

        private sealed record CachePolicyRule(
            Func<string, IReadOnlyDictionary<string, string>, bool> Predicate,
            CachePolicy Policy);
    }
}
