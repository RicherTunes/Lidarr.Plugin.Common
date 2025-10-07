using System;
using System.Collections.Generic;
using System.Linq;

namespace Lidarr.Plugin.Common.Utilities
{
    public static class QueryCanonicalizer
    {
        public static string Canonicalize(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            if (pairs == null) return string.Empty;
            var list = pairs as IList<KeyValuePair<string, string>> ?? pairs.ToList();
            if (list.Count == 0) return string.Empty;

            // Group by key (Ordinal), sort values Ordinal, join with comma, then percent-encode with lowercase hex.
            // Preserve empty pairs (b=) if present in inputs.
            var grouped = list
                .Select(p => new KeyValuePair<string, string>(p.Key ?? string.Empty, p.Value ?? string.Empty))
                .GroupBy(p => p.Key, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            var components = new List<string>(grouped.Count());
            foreach (var g in grouped)
            {
                var values = g.Select(kv => kv.Value ?? string.Empty)
                              .OrderBy(v => v, StringComparer.Ordinal)
                              .ToArray();
                var joined = string.Join(",", values);
                var keyEncoded = PercentEncodeLower(g.Key ?? string.Empty);
                var valueEncoded = PercentEncodeLower(joined);
                components.Add($"{keyEncoded}={valueEncoded}");
            }

            return string.Join("&", components);
        }

        public static string Canonicalize(IDictionary<string, IEnumerable<string>> map)
        {
            if (map == null || map.Count == 0) return string.Empty;

            var flattened = map
                .SelectMany(kvp => (kvp.Value ?? Enumerable.Empty<string>())
                    .Select(v => new KeyValuePair<string, string>(kvp.Key ?? string.Empty, v ?? string.Empty)));

            return Canonicalize(flattened);
        }

        private static string PercentEncodeLower(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var encoded = Uri.EscapeDataString(value);
            // Convert only hex digits in percent-escapes to lowercase
            // e.g., %2F instead of %2f -> we want lowercase -> %2f
            var sb = new System.Text.StringBuilder(encoded.Length);
            for (int i = 0; i < encoded.Length; i++)
            {
                var ch = encoded[i];
                if (ch == '%' && i + 2 < encoded.Length)
                {
                    sb.Append('%');
                    // Lowercase the two hex digits
                    sb.Append(char.ToLowerInvariant(encoded[i + 1]));
                    sb.Append(char.ToLowerInvariant(encoded[i + 2]));
                    i += 2;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }
    }
}
