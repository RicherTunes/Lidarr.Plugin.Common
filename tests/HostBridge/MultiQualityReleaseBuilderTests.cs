using System;
using System.Linq;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// TDD pins for <see cref="MultiQualityReleaseBuilder"/> — the "one release per available quality"
/// pattern emitted by tidalarr's <c>ConvertToReleaseInfosStatic</c> and qobuzarr's per-quality
/// parser loop. Verifies it composes <see cref="AlbumReleaseInfoBuilder"/> (distinct per-tier GUID
/// + title markers) and <see cref="AlbumSizeEstimator"/> (per-tier size) correctly.
/// </summary>
public class MultiQualityReleaseBuilderTests
{
    private static MultiQualityReleaseBuilder BaseBuilder() =>
        new MultiQualityReleaseBuilder()
            .WithArtist("Miles Davis")
            .WithAlbum("Kind of Blue")
            .WithYear(1959)
            .WithScheme("tidal")
            .WithAlbumId("12345")
            .WithDurationSeconds(2400);

    [Fact]
    public void Build_EmitsOneReleasePerQuality_InInsertionOrder()
    {
        var releases = BaseBuilder()
            .AddQuality("Lossless", "FLAC", null, 1_000 * 1000.0)
            .AddQuality("HiRes", "FLAC", "HIRES", 3_000 * 1000.0)
            .Build();

        Assert.Equal(2, releases.Count);
        Assert.Equal("Lossless", releases[0].QualityHint);
        Assert.Equal("HiRes", releases[1].QualityHint);
    }

    [Fact]
    public void Build_ProducesDistinctPerQualityGuidsAndUrls()
    {
        var releases = BaseBuilder()
            .AddQuality("Lossless", "FLAC", null, 1_000 * 1000.0)
            .AddQuality("HiRes", "FLAC", "HIRES", 3_000 * 1000.0)
            .Build();

        Assert.Equal("tidal:album:12345:Lossless", releases[0].Guid);
        Assert.Equal("tidal:album:12345:HiRes", releases[1].Guid);
        Assert.Equal("tidal://album/12345?quality=Lossless", releases[0].DownloadUrl);
        Assert.Equal("tidal://album/12345?quality=HiRes", releases[1].DownloadUrl);
        Assert.Equal(2, releases.Select(r => r.Guid).Distinct().Count());
    }

    [Fact]
    public void Build_ComposesTitleMarkersPerTier()
    {
        var releases = BaseBuilder()
            .AddQuality("Lossless", "FLAC", null, 1_000 * 1000.0)
            .AddQuality("HiRes", "FLAC", "HIRES", 3_000 * 1000.0)
            .Build();

        Assert.Equal("Miles Davis - Kind of Blue (1959) [FLAC] [WEB]", releases[0].Title);
        Assert.Equal("Miles Davis - Kind of Blue (1959) [FLAC] [HIRES] [WEB]", releases[1].Title);
    }

    [Fact]
    public void Build_EstimatesSizePerTier_FromDurationAndBitrate()
    {
        var releases = BaseBuilder()
            .AddQuality("Lossless", "FLAC", null, 1_000 * 1000.0)
            .AddQuality("HiRes", "FLAC", "HIRES", 3_000 * 1000.0)
            .Build();

        Assert.Equal(2400L * 1000 * 125, releases[0].SizeBytes); // 1000 kbps
        Assert.Equal(2400L * 3000 * 125, releases[1].SizeBytes); // 3000 kbps
    }

    [Fact]
    public void Build_AppliesAlbumLevelMarkersToEveryTier()
    {
        var releases = BaseBuilder()
            .WithEditionMarker("Deluxe")
            .WithExplicitMarker(true)
            .WithLiveMarker(true)
            .AddQuality("Lossless", "FLAC", null, 1_000 * 1000.0)
            .Build();

        Assert.Equal("Miles Davis - Kind of Blue (1959) [Deluxe] [Explicit] [LIVE] [FLAC] [WEB]", releases[0].Title);
    }

    [Fact]
    public void Build_HonorsCustomReleaseGroup()
    {
        var releases = BaseBuilder()
            .WithReleaseGroup("WEB-DL")
            .AddQuality("Lossless", "FLAC", null, 1_000 * 1000.0)
            .Build();

        Assert.EndsWith("[FLAC] [WEB-DL]", releases[0].Title);
    }

    [Fact]
    public void Build_AppliesMinimumSizeFloorPerTier()
    {
        var releases = new MultiQualityReleaseBuilder()
            .WithArtist("A").WithAlbum("B").WithScheme("qobuz").WithAlbumId("9")
            .WithDurationSeconds(0) // unknown duration → size falls to floor
            .WithMinimumSizeBytes(AlbumSizeEstimator.DefaultMinimumSizeBytes)
            .AddQuality("FLAC", "FLAC", null, 1_411_200)
            .Build();

        Assert.Equal(AlbumSizeEstimator.DefaultMinimumSizeBytes, releases[0].SizeBytes);
    }

    [Fact]
    public void Build_WithNoQualities_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => BaseBuilder().Build());
    }

    [Fact]
    public void Build_WithMissingRequiredAlbumField_Throws()
    {
        // No artist set → AlbumReleaseInfoBuilder rejects it.
        var builder = new MultiQualityReleaseBuilder()
            .WithAlbum("B").WithScheme("qobuz").WithAlbumId("9")
            .AddQuality("FLAC", "FLAC", null, 1_411_200);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void AddQuality_WithBlankHint_Throws()
    {
        Assert.Throws<ArgumentException>(() => BaseBuilder().AddQuality("  ", "FLAC", null, 1_000_000));
    }
}
