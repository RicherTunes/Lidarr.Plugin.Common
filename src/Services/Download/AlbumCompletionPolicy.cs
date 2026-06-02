namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// Canonical, side-effect-free album-completion rule shared by all streaming plugins.
    /// </summary>
    public static class AlbumCompletionPolicy
    {
        /// <summary>Default minimum success rate applied only to a COMPLETE album.</summary>
        public const double DefaultMinimumSuccessRate = 0.8;

        /// <summary>
        /// Determines whether an album download should be reported to the host as successful.
        /// </summary>
        /// <remarks>
        /// An album is successful ONLY when every track ended up on disk
        /// (<paramref name="successfulTracks"/> == <paramref name="totalTracks"/>). Any deficit —
        /// a failed track OR a sample/preview-skipped one — leaves the album incomplete, and
        /// Lidarr's <c>NoMissingOrUnmatchedTracksSpecification</c> permanently rejects an incomplete
        /// release ("Has missing tracks"), so reporting it Completed silently wastes the good files.
        /// Reporting failure instead lets Lidarr blocklist + re-search (fall back to another source).
        /// The <paramref name="minimumSuccessRate"/> / <paramref name="treatPreviewAsFailure"/> knobs
        /// can only ever gate a COMPLETE album — they can never rescue an incomplete one (the hard
        /// gate fires first). Pure function: no I/O, no host types — each plugin maps the result to
        /// its own download-item status at its boundary.
        /// </remarks>
        public static bool IsAlbumDownloadSuccessful(
            int totalTracks,
            int successfulTracks,
            int skippedTracks = 0,
            double minimumSuccessRate = DefaultMinimumSuccessRate,
            bool treatPreviewAsFailure = false,
            bool failOnNoTracksAvailable = true)
        {
            if (totalTracks == 0)
                return !failOnNoTracksAvailable;

            // Hard gate: an incomplete album is unimportable, so any missing track ⇒ not successful,
            // regardless of the threshold knobs below.
            if (successfulTracks < totalTracks)
                return false;

            var effectiveTotal = totalTracks;
            if (!treatPreviewAsFailure)
                effectiveTotal -= skippedTracks;

            if (effectiveTotal == 0)
                return !failOnNoTracksAvailable;

            var successRate = (double)successfulTracks / effectiveTotal;
            return successRate >= minimumSuccessRate;
        }
    }
}
