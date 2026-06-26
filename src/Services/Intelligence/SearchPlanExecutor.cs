using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Intelligence
{
    /// <summary>
    /// The execution half of the search-plan pipeline: runs the ordered fallback tiers produced by
    /// <see cref="SearchQuerySanitizer.BuildPlan(string, string, SanitizerOptions)"/> (or any pre-built
    /// tier list) against a caller-supplied <c>queryAsync</c> delegate, applying one of the
    /// three cross-plugin <see cref="SearchStopPolicy"/> stop policies and baking in the uniform
    /// all-failed-throw + cancellation contracts every streaming plugin shares.
    ///
    /// <para><b>Delegate-only.</b> The executor NEVER constructs, signs, or mutates a request — transport
    /// (the qobuz GET+signer pipeline, the apple ES256 JWT, the amazon ADP sign-over-final-body, the tidal
    /// <c>api.tidal.com</c> GET) lives entirely inside the <c>queryAsync</c> delegate. It does NO dedup and
    /// NO result-matching/ranking: callers keep their GUID dedup + <c>CleanupReleases</c> + model mapping
    /// AFTER the executor returns.</para>
    ///
    /// <para><b>Bookkeeping.</b> <c>attempted</c> counts every variant whose delegate was invoked;
    /// <c>succeeded</c> counts every delegate that returned WITHOUT throwing (independent of how many
    /// results it produced). The two are tracked separately: stop decisions key on
    /// <c>results.Count &gt; 0</c>, while the all-failed throw keys on <c>succeeded</c> — so a variant that
    /// returns an empty list counts as a success (genuine "no matches" returns <c>[]</c>) and never trips
    /// the throw.</para>
    /// </summary>
    public static class SearchPlanExecutor
    {
        /// <summary>
        /// Executes the pre-built <paramref name="tiers"/> under <paramref name="stopPolicy"/>.
        /// </summary>
        /// <typeparam name="TResult">The per-query result element (e.g. <c>ReleaseInfo</c>, <c>TidalAlbumInfo</c>, <c>StreamingAlbum</c>).</typeparam>
        /// <param name="tiers">Pre-built ordered fallback tiers (best-first variants per tier). The executor never calls <see cref="SearchQuerySanitizer.BuildPlan(string, string, SanitizerOptions)"/> itself.</param>
        /// <param name="queryAsync">The per-variant search delegate; owns all transport. Returns the (possibly empty) result list for that variant.</param>
        /// <param name="stopPolicy">Required — each caller passes its current behavior; no plugin is forced to change.</param>
        /// <param name="onError">Invoked once per FAILED variant (never on cancellation). The loop continues so a transient failure on an early tier can still be rescued by a later one.</param>
        /// <param name="serviceLabel">Templated into the all-failed message; defaults to <c>"search"</c>.</param>
        /// <param name="cancellationToken">Checked at the top of every variant iteration; a cancellation aborts and propagates, and is never recorded as a failed variant.</param>
        /// <exception cref="InvalidOperationException">When every attempted variant failed (<c>attempted &gt; 0 &amp;&amp; succeeded == 0 &amp;&amp; lastError != null</c>); wraps the last error.</exception>
        public static async Task<IReadOnlyList<TResult>> ExecuteAsync<TResult>(
            IReadOnlyList<IReadOnlyList<string>> tiers,
            Func<string, CancellationToken, Task<IReadOnlyList<TResult>>> queryAsync,
            SearchStopPolicy stopPolicy,
            Action<string, Exception>? onError = null,
            string? serviceLabel = null,
            CancellationToken cancellationToken = default)
        {
            if (queryAsync == null)
            {
                throw new ArgumentNullException(nameof(queryAsync));
            }

            var results = new List<TResult>();
            var attempted = 0;
            var succeeded = 0;
            Exception? lastError = null;

            foreach (var tier in tiers ?? Array.Empty<IReadOnlyList<string>>())
            {
                if (tier == null)
                {
                    continue;
                }

                var tierProducedResults = false;

                foreach (var variant in tier)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    attempted++;

                    IReadOnlyList<TResult>? variantResults;
                    try
                    {
                        variantResults = await queryAsync(variant, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Genuine cancellation: propagate (never recorded as a failed variant nor sent to
                        // onError). Inert for callers that pass CancellationToken.None — an inner
                        // TaskCanceledException then falls through to the general catch as a recoverable failure.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        onError?.Invoke(variant, ex);
                        continue;
                    }

                    succeeded++;

                    if (variantResults != null && variantResults.Count > 0)
                    {
                        results.AddRange(variantResults);
                        tierProducedResults = true;

                        if (stopPolicy == SearchStopPolicy.StopAfterFirstVariantWithResults)
                        {
                            return results;
                        }
                    }
                }

                if (tierProducedResults && stopPolicy == SearchStopPolicy.StopAfterFirstTierWithResults)
                {
                    break;
                }
            }

            ThrowAllFailed(attempted, succeeded, lastError, serviceLabel);
            return results;
        }

        /// <summary>
        /// Convenience overload that forwards <see cref="SearchPlan.Tiers"/> from a built plan.
        /// </summary>
        public static Task<IReadOnlyList<TResult>> ExecuteAsync<TResult>(
            SearchPlan plan,
            Func<string, CancellationToken, Task<IReadOnlyList<TResult>>> queryAsync,
            SearchStopPolicy stopPolicy,
            Action<string, Exception>? onError = null,
            string? serviceLabel = null,
            CancellationToken cancellationToken = default)
            => ExecuteAsync(
                plan?.Tiers ?? Array.Empty<IReadOnlyList<string>>(),
                queryAsync,
                stopPolicy,
                onError,
                serviceLabel,
                cancellationToken);

        /// <summary>
        /// The shared all-failed contract: when every attempted request failed
        /// (<paramref name="attempted"/> &gt; 0 &amp;&amp; <paramref name="succeeded"/> == 0 &amp;&amp;
        /// <paramref name="lastError"/> is non-null) throw an <see cref="InvalidOperationException"/>
        /// wrapping the last error instead of returning an empty result. Extracted so a plugin whose loop
        /// is the Lidarr host's request chain (qobuz) can call the SAME contract its FetchReleases relies on,
        /// and the executor uses it internally.
        /// </summary>
        public static void ThrowAllFailed(int attempted, int succeeded, Exception? lastError, string? serviceLabel)
        {
            if (attempted > 0 && succeeded == 0 && lastError != null)
            {
                throw new InvalidOperationException(
                    $"All {attempted} {serviceLabel ?? "search"} request(s) failed; surfacing the error instead of an empty result.",
                    lastError);
            }
        }
    }
}
