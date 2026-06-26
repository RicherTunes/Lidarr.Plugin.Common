namespace Lidarr.Plugin.Common.Services.Intelligence
{
    /// <summary>
    /// How <see cref="SearchPlanExecutor"/> decides when to stop issuing tier variants. There is no
    /// implicit default — each caller passes its CURRENT behavior so no plugin is forced to change. The
    /// three members are exactly the stop semantics observed across the streaming plugins.
    /// </summary>
    public enum SearchStopPolicy
    {
        /// <summary>
        /// Attempt every variant in every tier and merge all results — no early stop (qobuz, apple).
        /// </summary>
        AccumulateAll,

        /// <summary>
        /// Attempt all variants in a tier; once a tier yields at least one result, skip the remaining
        /// (lower-priority) fallback tiers (tidal).
        /// </summary>
        StopAfterFirstTierWithResults,

        /// <summary>
        /// Stop the instant a single variant yields at least one result (amazon).
        /// </summary>
        StopAfterFirstVariantWithResults,
    }
}
