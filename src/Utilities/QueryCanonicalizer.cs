using System;
using System.Collections.Generic;
using System.Linq;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static class QueryCanonicalizer
    {
        public static string Canonicalize(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            if (pairs == null) return string.Empty;
            var list = pairs as IList<KeyValuePair<string, string>> ?? pairs.ToList();
            if (list.Count == 0) return string.Empty;

            var ordered = list
                .Select(p => new KeyValuePair<string, string>(p.Key ?? string.Empty, p.Value ?? string.Empty))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal)
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}");

            return string.Join("&", ordered);
        }

        public static string Canonicalize(IDictionary<string, IEnumerable<string>> map)
        {
            if (map == null || map.Count == 0) return string.Empty;

            var flattened = map
                .SelectMany(kvp => (kvp.Value ?? Enumerable.Empty<string>())
                    .Select(v => new KeyValuePair<string, string>(kvp.Key ?? string.Empty, v ?? string.Empty)));

            return Canonicalize(flattened);
        }
    }
}

