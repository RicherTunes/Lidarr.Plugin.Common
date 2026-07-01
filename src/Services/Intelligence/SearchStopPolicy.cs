namespace Lidarr.Plugin.Common.Services.Intelligence
{
    /// <summary>
    /// How <see cref="SearchPlanExecutor"/> decides when to stop issuing tier variants. There is no
    /// implicit default — each caller passes its CURRENT behavior so no plugin is forced to change. The
    /// three real members are exactly the stop semantics observed across the streaming plugins;
    /// <see cref="Unknown"/> is the uninitialized default and is REJECTED by the executor so a forgotten
    /// policy fails loudly instead of silently behaving like <see cref="AccumulateAll"/>.
    /// </summary>
    public enum SearchStopPolicy
    {
        /// <summary>
        /// Not a real policy — the zero/uninitialized default. The executor throws
        /// <see cref="System.ArgumentOutOfRangeException"/> when handed this (or any undefined value) so a
        /// caller that forgets to pass a concrete policy can't accidentally accumulate-all.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Attempt every variant in every tier and merge all results — no early stop (qobuz, apple).
        /// </summary>
        AccumulateAll = 1,

        /// <summary>
        /// Attempt all variants in a tier; once a tier yields at least one result, skip the remaining
        /// (lower-priority) fallback tiers (tidal).
        /// </summary>
        StopAfterFirstTierWithResults = 2,

        /// <summary>
        /// Stop the instant a single variant yields at least one result (amazon).
        /// </summary>
        StopAfterFirstVariantWithResults = 3,
    }
}
