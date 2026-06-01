using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.HostBridge;

/// <summary>
/// Estimates the on-disk byte size of a streaming album/track from its duration and bitrate.
/// Every RicherTunes streaming-service plugin computes a <c>ReleaseInfo.Size</c> the same way
/// — <c>bytes = durationSeconds × (bitrate ÷ 8)</c>, optionally floored — but each rolled its
/// own copy (tidalarr's <c>EstimateAlbumSize</c>, qobuzarr's <c>QualitySizeCalculator</c>).
///
/// <para><strong>Unit convention:</strong> bitrate is expressed in <em>bits per second</em> as a
/// <see cref="double"/>. This is deliberate — Qobuz's per-quality bitrates are not round kbps
/// (e.g. FLAC ≈ 1 411 200 bps = 1411.2 kbps), so an <c>int kbps</c> parameter would truncate and
/// shift the computed size. Callers holding kbps pass <c>kbps × 1000</c>.</para>
///
/// <para>The arithmetic is done in <see cref="double"/> and cast once at the end, which also
/// sidesteps the latent <c>int × int × int</c> overflow a kbps-based integer formula hits on long
/// hi-res albums (e.g. a 2-hour 24-bit/96 kHz album exceeds <see cref="int.MaxValue"/>).</para>
/// </summary>
public static class AlbumSizeEstimator
{
    /// <summary>
    /// Conventional 1 MB floor. Lidarr can mishandle a zero/near-zero release size, so plugins
    /// that want a non-zero guarantee pass this (or their own value) as <c>minimumBytes</c>.
    /// </summary>
    public const long DefaultMinimumSizeBytes = 1024L * 1024L;

    /// <summary>
    /// Estimate the byte size of audio of the given duration at the given bitrate.
    /// </summary>
    /// <param name="durationSeconds">Total playback duration in seconds.</param>
    /// <param name="bitsPerSecond">Encoded bitrate in bits per second (not kbps).</param>
    /// <param name="minimumBytes">
    /// Lower bound on the returned size. When the computed size is below this (including the
    /// degenerate case of a non-positive duration or bitrate), <paramref name="minimumBytes"/> is
    /// returned instead. Defaults to 0 (no floor). Negative values are treated as 0.
    /// </param>
    /// <returns>Estimated size in bytes, never less than <paramref name="minimumBytes"/>.</returns>
    public static long EstimateBytesFromBitrate(double durationSeconds, double bitsPerSecond, long minimumBytes = 0)
    {
        if (minimumBytes < 0)
        {
            minimumBytes = 0;
        }

        if (durationSeconds <= 0 || bitsPerSecond <= 0)
        {
            return minimumBytes;
        }

        var bytes = (long)(durationSeconds * (bitsPerSecond / 8.0));
        return Math.Max(bytes, minimumBytes);
    }

    /// <summary>
    /// Resolve the most reliable album duration from the data a plugin has, using the shared
    /// fallback ladder: (1) an explicit album duration, else (2) the sum of per-track durations,
    /// else (3) a count × per-track-average estimate — never below <paramref name="minimumSeconds"/>.
    /// </summary>
    /// <param name="albumDurationSeconds">Album-level duration if known; ignored when ≤ 0.</param>
    /// <param name="trackDurationsSeconds">
    /// Per-track durations in seconds. Non-positive entries are ignored. When the collection is
    /// null/empty or every entry is non-positive, the ladder falls through to the count estimate.
    /// </param>
    /// <param name="fallbackTrackCount">Track count used by the count estimate; coerced to ≥ 1.</param>
    /// <param name="averageTrackSeconds">Assumed average track length for the count estimate.</param>
    /// <param name="minimumSeconds">Floor for the count estimate (e.g. 30s). Ignored when ≤ 0.</param>
    public static double EstimateDurationSeconds(
        double albumDurationSeconds,
        IReadOnlyCollection<double>? trackDurationsSeconds,
        int fallbackTrackCount,
        double averageTrackSeconds,
        double minimumSeconds = 0)
    {
        if (albumDurationSeconds > 0)
        {
            return albumDurationSeconds;
        }

        if (trackDurationsSeconds is { Count: > 0 })
        {
            double sum = 0;
            foreach (var d in trackDurationsSeconds)
            {
                if (d > 0)
                {
                    sum += d;
                }
            }

            if (sum > 0)
            {
                return sum;
            }
        }

        var count = fallbackTrackCount > 0 ? fallbackTrackCount : 1;
        var avg = averageTrackSeconds > 0 ? averageTrackSeconds : 0;
        var estimate = count * avg;
        return minimumSeconds > 0 ? Math.Max(estimate, minimumSeconds) : estimate;
    }
}
