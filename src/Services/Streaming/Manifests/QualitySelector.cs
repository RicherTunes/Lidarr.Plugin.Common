using System;
using System.Collections.Generic;
using System.Linq;

namespace Lidarr.Plugin.Common.Services.Streaming.Manifests
{
    /// <summary>
    /// Protocol-neutral helpers for picking a <see cref="StreamVariant"/> from a parsed manifest's
    /// rendition list. Selection is by declared <see cref="StreamVariant.BandwidthBps"/> only; this
    /// type performs no fetching and no decryption.
    /// </summary>
    public static class QualitySelector
    {
        /// <summary>
        /// Returns the highest-bandwidth variant whose <see cref="StreamVariant.BandwidthBps"/> is
        /// less than or equal to <paramref name="ceilingBps"/>. When every variant exceeds the ceiling
        /// (so none qualifies), returns the single lowest-bandwidth variant so a playable stream is
        /// always chosen rather than nothing. Returns <c>null</c> only when <paramref name="variants"/>
        /// is null or empty.
        /// </summary>
        /// <param name="variants">Candidate renditions (e.g. from <see cref="StreamManifest.Variants"/>).</param>
        /// <param name="ceilingBps">Inclusive upper bound on bandwidth, in bits per second.</param>
        public static StreamVariant? SelectByBandwidthCeiling(IReadOnlyList<StreamVariant> variants, int ceilingBps)
        {
            if (variants == null || variants.Count == 0)
            {
                return null;
            }

            StreamVariant? best = null;
            foreach (StreamVariant v in variants)
            {
                if (v.BandwidthBps <= ceilingBps && (best == null || v.BandwidthBps > best.BandwidthBps))
                {
                    best = v;
                }
            }

            if (best != null)
            {
                return best;
            }

            // Nothing at or below the ceiling: fall back to the lowest available so playback can proceed.
            StreamVariant lowest = variants[0];
            foreach (StreamVariant v in variants.Skip(1))
            {
                if (v.BandwidthBps < lowest.BandwidthBps)
                {
                    lowest = v;
                }
            }

            return lowest;
        }
    }
}
