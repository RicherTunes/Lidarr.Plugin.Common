using System;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Canonical string-similarity primitives (LOOP-010). Edit-distance and Jaro / Jaro-Winkler matchers
    /// are pure, stateless functions that several plugins re-implemented identically (qobuzarr's
    /// <c>StringSimilarity</c>, brainarr's duplicate detectors). They live here so there is one tested
    /// implementation. For Unicode-aware normalized similarity see
    /// <c>Lidarr.Plugin.Common.Services.Globalization.UnicodeNormalizer</c>.
    /// </summary>
    public static class StringSimilarity
    {
        /// <summary>
        /// Levenshtein edit distance: the minimum number of single-character insertions, deletions, or
        /// substitutions to turn <paramref name="s1"/> into <paramref name="s2"/>. Null/empty is treated
        /// as a zero-length string (distance = the other string's length).
        /// </summary>
        public static int LevenshteinDistance(string? s1, string? s2)
        {
            if (string.IsNullOrEmpty(s1))
                return string.IsNullOrEmpty(s2) ? 0 : s2!.Length;

            if (string.IsNullOrEmpty(s2))
                return s1!.Length;

            var n = s1!.Length;
            var m = s2!.Length;
            var d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[n, m];
        }

        /// <summary>
        /// Jaro similarity in [0.0, 1.0]. Empty/empty is 1.0; empty/non-empty is 0.0; case-insensitively
        /// equal strings are 1.0.
        /// </summary>
        public static double Jaro(string? s1, string? s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
                return 1.0;

            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0.0;

            if (s1!.Equals(s2, StringComparison.OrdinalIgnoreCase))
                return 1.0;

            var s1Length = s1.Length;
            var s2Length = s2!.Length;

            var matchDistance = Math.Max(s1Length, s2Length) / 2 - 1;
            var s1Matches = new bool[s1Length];
            var s2Matches = new bool[s2Length];

            var matches = 0;
            var transpositions = 0;

            for (int i = 0; i < s1Length; i++)
            {
                var start = Math.Max(0, i - matchDistance);
                var end = Math.Min(i + matchDistance + 1, s2Length);

                for (int j = start; j < end; j++)
                {
                    if (s2Matches[j] || s1[i] != s2[j])
                        continue;

                    s1Matches[i] = true;
                    s2Matches[j] = true;
                    matches++;
                    break;
                }
            }

            if (matches == 0)
                return 0.0;

            var k = 0;
            for (int i = 0; i < s1Length; i++)
            {
                if (!s1Matches[i])
                    continue;

                while (!s2Matches[k])
                    k++;

                if (s1[i] != s2[k])
                    transpositions++;

                k++;
            }

            return (matches / (double)s1Length +
                    matches / (double)s2Length +
                    (matches - transpositions / 2.0) / matches) / 3.0;
        }

        /// <summary>
        /// Jaro-Winkler similarity: <see cref="Jaro"/> plus a bonus for a shared leading prefix (up to 4
        /// chars). The bonus is only applied when the Jaro score is at least 0.7 (the standard threshold);
        /// below that the plain Jaro score is returned.
        /// </summary>
        public static double JaroWinkler(string? s1, string? s2, double prefixScale = 0.1)
        {
            var jaro = Jaro(s1, s2);

            if (jaro < 0.7)
                return jaro;

            var prefixLength = 0;
            var maxPrefix = Math.Min(Math.Min(s1?.Length ?? 0, s2?.Length ?? 0), 4);

            for (int i = 0; i < maxPrefix; i++)
            {
                if (s1![i] == s2![i])
                    prefixLength++;
                else
                    break;
            }

            return jaro + prefixLength * prefixScale * (1 - jaro);
        }
    }
}
