using System.Collections.Generic;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// TDD pins for <see cref="AlbumSizeEstimator"/> — the shared duration×bitrate→bytes kernel
/// that tidalarr (<c>EstimateAlbumSize</c>) and qobuzarr (<c>QualitySizeCalculator</c>) each
/// hand-rolled. The equivalence tests assert the lifted helper reproduces both plugins' formulas
/// byte-for-byte so adoption is provably behavior-preserving.
/// </summary>
public class AlbumSizeEstimatorTests
{
    [Fact]
    public void EstimateBytesFromBitrate_BasicCase_MultipliesDurationByBytesPerSecond()
    {
        // 200s @ 1,411,200 bps → 200 * (1411200/8) = 200 * 176400 = 35,280,000
        Assert.Equal(35_280_000L, AlbumSizeEstimator.EstimateBytesFromBitrate(200, 1_411_200));
    }

    [Fact]
    public void EstimateBytesFromBitrate_MatchesTidalIntegerFormula_ForRepresentativeAlbum()
    {
        // tidal: totalDurationSeconds * bitrateKbps * 125  (kbps×125 = kbps×1000/8 = bytes/sec)
        // here 2400s @ 3000 kbps → 2400 * 3000 * 125 = 900,000,000
        const long tidalFormula = 2400L * 3000L * 125L;
        Assert.Equal(tidalFormula, AlbumSizeEstimator.EstimateBytesFromBitrate(2400, 3_000 * 1000.0));
    }

    [Fact]
    public void EstimateBytesFromBitrate_MatchesQobuzDoubleFormula_ForFlacLossless()
    {
        // qobuz: (long)(durationSeconds * (bitrate / 8.0)) with bitrate = 1,411,200 bps
        const double duration = 3600;
        const double bits = 1_411_200;
        var qobuzFormula = (long)(duration * (bits / 8.0));
        Assert.Equal(qobuzFormula, AlbumSizeEstimator.EstimateBytesFromBitrate(duration, bits));
    }

    [Theory]
    [InlineData(0, 1_000_000)]      // zero duration
    [InlineData(-5, 1_000_000)]     // negative duration
    public void EstimateBytesFromBitrate_NonPositiveDuration_ReturnsFloor(double duration, double bits)
    {
        Assert.Equal(500L, AlbumSizeEstimator.EstimateBytesFromBitrate(duration, bits, minimumBytes: 500));
    }

    [Fact]
    public void EstimateBytesFromBitrate_NonPositiveBitrate_ReturnsFloor()
    {
        Assert.Equal(500L, AlbumSizeEstimator.EstimateBytesFromBitrate(100, 0, minimumBytes: 500));
    }

    [Fact]
    public void EstimateBytesFromBitrate_ComputedBelowFloor_ReturnsFloor()
    {
        // 1s @ 8 bps → 1 byte, below 1 MB floor
        Assert.Equal(AlbumSizeEstimator.DefaultMinimumSizeBytes,
            AlbumSizeEstimator.EstimateBytesFromBitrate(1, 8, AlbumSizeEstimator.DefaultMinimumSizeBytes));
    }

    [Fact]
    public void EstimateBytesFromBitrate_NoFloorByDefault_ReturnsRawEstimate()
    {
        Assert.Equal(100L, AlbumSizeEstimator.EstimateBytesFromBitrate(1, 800));
    }

    [Fact]
    public void EstimateBytesFromBitrate_NegativeFloor_TreatedAsZero()
    {
        Assert.Equal(10_000L, AlbumSizeEstimator.EstimateBytesFromBitrate(100, 800, minimumBytes: -10));
    }

    [Fact]
    public void EstimateDurationSeconds_PrefersExplicitAlbumDuration()
    {
        var result = AlbumSizeEstimator.EstimateDurationSeconds(1800, new[] { 100.0, 200.0 }, 12, 240, 30);
        Assert.Equal(1800, result);
    }

    [Fact]
    public void EstimateDurationSeconds_SumsTrackDurations_IgnoringNonPositive()
    {
        var result = AlbumSizeEstimator.EstimateDurationSeconds(0, new[] { 100.0, 200.0, 0.0, -5.0 }, 12, 240, 30);
        Assert.Equal(300, result);
    }

    [Fact]
    public void EstimateDurationSeconds_FallsBackToCountTimesAverage()
    {
        var result = AlbumSizeEstimator.EstimateDurationSeconds(0, null, 10, 240, 30);
        Assert.Equal(2400, result);
    }

    [Fact]
    public void EstimateDurationSeconds_AllTracksZero_FallsBackToCountEstimate()
    {
        var result = AlbumSizeEstimator.EstimateDurationSeconds(0, new[] { 0.0, 0.0 }, 5, 60, 30);
        Assert.Equal(300, result);
    }

    [Fact]
    public void EstimateDurationSeconds_AppliesMinimumFloor()
    {
        // count coerced to 1, average 0 → 0, floored to 30
        var result = AlbumSizeEstimator.EstimateDurationSeconds(0, null, 0, 0, 30);
        Assert.Equal(30, result);
    }
}
