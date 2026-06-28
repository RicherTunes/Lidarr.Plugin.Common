using System;
using System.Collections.Generic;
using System.Linq;

namespace Lidarr.Plugin.Common.Services.Intelligence
{
    /// <summary>
    /// The "capped search chain" policy: cap the number of over-specific (combined / album-only) queries
    /// issued per search to keep API calls bounded, while ALWAYS preserving the artist-only catalogue
    /// fallback — it is appended <i>in addition</i> to the cap and is never truncated away.
    ///
    /// <para>This is the reusable form of qobuz's bespoke
    /// <c>QobuzRequestGenerator.CreateIndexerRequests</c> logic. The fallback-survival guarantee is the fix
    /// for the shipped "Bleu Jeans Bleu - Record n°V" bug, where a special-char album query returned 0
    /// results while the artist-only fallback had been truncated away by the request cap. Promoting it here
    /// makes any capping plugin inherit the guarantee with cross-plugin test coverage rather than re-deriving
    /// (and re-breaking) it.</para>
    ///
    /// <para><b>Construction-only.</b> This selects which query strings to issue; it does not build, sign, or
    /// execute requests, and it does not model the all-failed / partial-success outcome contract — that is
    /// <see cref="SearchPlanExecutor.ThrowAllFailed"/>, which capping plugins reuse unchanged.</para>
    /// </summary>
    public static class CappedSearchChain
    {
        /// <summary>
        /// Builds the final ordered query list: at most <paramref name="maxOverSpecific"/> of the
        /// (de-duplicated, blank-dropped, best-first) <paramref name="overSpecificQueries"/>, then — if
        /// non-blank and not already present — the <paramref name="artistOnlyFallback"/> appended last.
        /// </summary>
        /// <param name="overSpecificQueries">Best-first combined / album-only query candidates. Null entries
        /// and blank/whitespace entries are dropped; duplicates are removed (first occurrence wins).</param>
        /// <param name="artistOnlyFallback">The artist-only catalogue fallback. Appended last and never
        /// subject to the cap. Ignored when null/blank or already present in the capped set.</param>
        /// <param name="maxOverSpecific">Maximum over-specific queries to issue (values &lt;= 0 issue none,
        /// leaving only the fallback).</param>
        /// <param name="comparer">Equality used for de-dup + the fallback presence check. Defaults to
        /// <see cref="StringComparer.OrdinalIgnoreCase"/>.</param>
        public static IReadOnlyList<string> Build(
            IEnumerable<string> overSpecificQueries,
            string? artistOnlyFallback,
            int maxOverSpecific,
            IEqualityComparer<string>? comparer = null)
        {
            comparer ??= StringComparer.OrdinalIgnoreCase;

            // De-duplicate and drop blank queries while preserving best-first order.
            var ordered = new List<string>();
            foreach (var query in overSpecificQueries ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(query) && !ordered.Contains(query, comparer))
                {
                    ordered.Add(query);
                }
            }

            var selected = ordered.Take(Math.Max(0, maxOverSpecific)).ToList();

            // Guarantee the artist-only fallback is always issued (never truncated by the cap).
            if (!string.IsNullOrWhiteSpace(artistOnlyFallback) && !selected.Contains(artistOnlyFallback, comparer))
            {
                selected.Add(artistOnlyFallback);
            }

            return selected;
        }
    }
}
