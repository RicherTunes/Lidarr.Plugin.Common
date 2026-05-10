using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.Services.Quality;
using Lidarr.Plugin.Common.TestKit.Builders;
using Lidarr.Plugin.Abstractions.Models;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services;

public class QualityMapperCovTests
{
    [Fact]
    public void GetQualityTier_NullQuality_ReturnsLow()
    {
        StreamingQuality? q = null;
        var result = QualityMapper.GetQualityTier(q!);
        Assert.Equal(StreamingQualityTier.Low, result);
    }

    [Fact]
    public void GetQualityTier_HiResQuality_ReturnsHiRes()
    {
        var q = StreamingQualityBuilder.CreateFlacHiRes();
        var result = QualityMapper.GetQualityTier(q);
        Assert.Equal(StreamingQualityTier.HiRes, result);
    }

    [Fact]
    public void GetQualityTier_LosslessCdQuality_ReturnsLossless()
    {
        var q = StreamingQualityBuilder.CreateFlacCd();
        var result = QualityMapper.GetQualityTier(q);
        Assert.Equal(StreamingQualityTier.Lossless, result);
    }

    [Fact]
    public void GetQualityTier_HighBitrate320_ReturnsHigh()
    {
        var q = StreamingQualityBuilder.CreateMp3320();
        var result = QualityMapper.GetQualityTier(q);
        Assert.Equal(StreamingQualityTier.High, result);
    }

    [Fact]
    public void GetQualityTier_NormalBitrate256_ReturnsNormal()
    {
        var q = StreamingQualityBuilder.CreateAac256();
        var result = QualityMapper.GetQualityTier(q);
        Assert.Equal(StreamingQualityTier.Normal, result);
    }

    [Fact]
    public void GetQualityTier_LowBitrate128_ReturnsLow()
    {
        var q = new StreamingQualityBuilder()
            .WithFormat("MP3")
            .WithBitrate(128)
            .WithSampleRate(null)
            .WithBitDepth(null)
            .Build();
        var result = QualityMapper.GetQualityTier(q);
        Assert.Equal(StreamingQualityTier.Low, result);
    }

    [Fact]
    public void GetQualityTier_NoBitrateLossy_ReturnsNormalFallback()
    {
        var q = new StreamingQualityBuilder()
            .WithFormat("AAC")
            .WithBitrate(null)
            .WithSampleRate(null)
            .WithBitDepth(null)
            .Build();
        var result = QualityMapper.GetQualityTier(q);
        Assert.Equal(StreamingQualityTier.Normal, result);
    }

    [Fact]
    public void FindBestMatch_NullCollection_ReturnsNull()
    {
        IEnumerable<StreamingQuality>? qualities = null;
        var result = QualityMapper.FindBestMatch(qualities!);
        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatch_EmptyCollection_ReturnsNull()
    {
        var result = QualityMapper.FindBestMatch(Enumerable.Empty<StreamingQuality>());
        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatch_ExactTierMatch_ReturnsBestOfThatTier()
    {
        var qualities = new List<StreamingQuality>
        {
            StreamingQualityBuilder.CreateFlacCd(),
            StreamingQualityBuilder.CreateMp3320(),
        };
        var result = QualityMapper.FindBestMatch(qualities, StreamingQualityTier.Lossless);
        Assert.Equal("flac-cd", result.Id);
    }

    [Fact]
    public void FindBestMatch_NoExactMatch_FindsHigherQuality()
    {
        var qualities = new List<StreamingQuality>
        {
            StreamingQualityBuilder.CreateFlacHiRes(),
        };
        var result = QualityMapper.FindBestMatch(qualities, StreamingQualityTier.Lossless);
        Assert.Equal("flac-hires", result.Id);
    }

    [Fact]
    public void FindBestMatch_NoHigherAvailable_FindsLowerQuality()
    {
        var qualities = new List<StreamingQuality>
        {
            StreamingQualityBuilder.CreateMp3320(),
        };
        var result = QualityMapper.FindBestMatch(qualities, StreamingQualityTier.HiRes);
        Assert.Equal("mp3-320", result.Id);
    }

    [Fact]
    public void FindBestMatch_SingleQuality_ReturnsThatQuality()
    {
        var qualities = new List<StreamingQuality> { StreamingQualityBuilder.CreateMp3320() };
        var result = QualityMapper.FindBestMatch(qualities, StreamingQualityTier.High);
        Assert.Equal("mp3-320", result.Id);
    }

    [Fact]
    public void CompareQualities_BothNull_ReturnsZero()
    {
        StreamingQuality? q1 = null;
        StreamingQuality? q2 = null;
        var result = QualityMapper.CompareQualities(q1!, q2!);
        Assert.Equal(0, result);
    }

    [Fact]
    public void CompareQualities_FirstNull_ReturnsNegativeOne()
    {
        StreamingQuality? q1 = null;
        var q2 = StreamingQualityBuilder.CreateMp3320();
        var result = QualityMapper.CompareQualities(q1!, q2);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void CompareQualities_SecondNull_ReturnsOne()
    {
        var q1 = StreamingQualityBuilder.CreateMp3320();
        StreamingQuality? q2 = null;
        var result = QualityMapper.CompareQualities(q1, q2!);
        Assert.Equal(1, result);
    }

    [Fact]
    public void CompareQualities_DifferentTiers_ReturnsTierComparison()
    {
        var q1 = StreamingQualityBuilder.CreateMp3320();
        var q2 = StreamingQualityBuilder.CreateFlacCd();
        var result = QualityMapper.CompareQualities(q1, q2);
        Assert.True(result < 0);
    }

    [Fact]
    public void CompareQualities_SameTier_DifferentBitDepth_ReturnsBitDepthComparison()
    {
        var q1 = StreamingQualityBuilder.CreateFlacHiRes();
        var q2 = StreamingQualityBuilder.CreateFlacUltraHiRes();
        var result = QualityMapper.CompareQualities(q1, q2);
        Assert.True(result < 0);
    }

    [Fact]
    public void CompareQualities_SameTier_SameBitDepth_DifferentSampleRate()
    {
        var q1 = new StreamingQualityBuilder()
            .WithFormat("FLAC").WithSampleRate(96000).WithBitDepth(24).Build();
        var q2 = new StreamingQualityBuilder()
            .WithFormat("FLAC").WithSampleRate(192000).WithBitDepth(24).Build();
        var result = QualityMapper.CompareQualities(q1, q2);
        Assert.True(result < 0);
    }

    [Fact]
    public void CompareQualities_SameTier_SameSpecs_DifferentBitrate()
    {
        var q1 = new StreamingQualityBuilder()
            .WithFormat("MP3").WithBitrate(256).WithSampleRate(null).WithBitDepth(null).Build();
        var q2 = new StreamingQualityBuilder()
            .WithFormat("MP3").WithBitrate(320).WithSampleRate(null).WithBitDepth(null).Build();
        var result = QualityMapper.CompareQualities(q1, q2);
        Assert.True(result < 0);
    }

    [Fact]
    public void CreatePreferenceMap_AllowBoth_SetsCorrectBounds()
    {
        var map = QualityMapper.CreatePreferenceMap(StreamingQualityTier.Lossless);
        Assert.Equal(StreamingQualityTier.Lossless, map.PreferredTier);
        Assert.True(map.AllowHigherQuality);
        Assert.True(map.AllowLowerQuality);
        Assert.Equal(StreamingQualityTier.HiRes, map.MaxAcceptableTier);
        Assert.Equal(StreamingQualityTier.Low, map.MinAcceptableTier);
    }

    [Fact]
    public void CreatePreferenceMap_NoHigher_SetsMaxToPreferred()
    {
        var map = QualityMapper.CreatePreferenceMap(StreamingQualityTier.High, allowHigher: false);
        Assert.Equal(StreamingQualityTier.High, map.MaxAcceptableTier);
    }

    [Fact]
    public void CreatePreferenceMap_NoLower_SetsMinToPreferred()
    {
        var map = QualityMapper.CreatePreferenceMap(StreamingQualityTier.Normal, allowLower: false);
        Assert.Equal(StreamingQualityTier.Normal, map.MinAcceptableTier);
    }

    [Fact]
    public void IsAcceptable_WithinBounds_ReturnsTrue()
    {
        var map = QualityMapper.CreatePreferenceMap(StreamingQualityTier.Lossless);
        Assert.True(map.IsAcceptable(StreamingQualityTier.Lossless));
        Assert.True(map.IsAcceptable(StreamingQualityTier.Low));
        Assert.True(map.IsAcceptable(StreamingQualityTier.HiRes));
    }

    [Fact]
    public void GetPreferenceScore_PerfectMatch_Returns100()
    {
        var map = QualityMapper.CreatePreferenceMap(StreamingQualityTier.Lossless);
        Assert.Equal(100, map.GetPreferenceScore(StreamingQualityTier.Lossless));
    }

    [Fact]
    public void GetPreferenceScore_HigherTierWithAllow_Returns90MinusDistance()
    {
        var map = QualityMapper.CreatePreferenceMap(StreamingQualityTier.Normal);
        Assert.Equal(87, map.GetPreferenceScore(StreamingQualityTier.HiRes));
    }

    [Fact]
    public void GetPreferenceScore_LowerTierWithAllow_Returns80MinusDistanceTimes10()
    {
        var map = QualityMapper.CreatePreferenceMap(StreamingQualityTier.High);
        Assert.Equal(60, map.GetPreferenceScore(StreamingQualityTier.Low));
    }

    [Fact]
    public void GetPreferenceScore_UnacceptableTier_ReturnsNegative1()
    {
        var map = QualityMapper.CreatePreferenceMap(
            StreamingQualityTier.High, allowHigher: false, allowLower: false);
        Assert.Equal(-1, map.GetPreferenceScore(StreamingQualityTier.HiRes));
    }

    [Fact]
    public void GetPreferenceScore_NeitherHigherNorLower_Returns0()
    {
        var map2 = new QualityPreferenceMap
        {
            PreferredTier = StreamingQualityTier.High,
            AllowHigherQuality = false,
            AllowLowerQuality = true,
            MaxAcceptableTier = StreamingQualityTier.HiRes,
            MinAcceptableTier = StreamingQualityTier.Low
        };
        Assert.Equal(0, map2.GetPreferenceScore(StreamingQualityTier.HiRes));
    }

    [Theory]
    [InlineData(5, "5", "MP3 320kbps", "MP3")]
    [InlineData(6, "6", "FLAC CD", "FLAC")]
    [InlineData(7, "7", "FLAC Hi-Res", "FLAC")]
    [InlineData(27, "27", "FLAC Studio Master", "FLAC")]
    public void FromNumericId_KnownIds_ReturnsCorrectMapping(
        int id, string expectedId, string expectedName, string expectedFormat)
    {
        var result = QualityMapper.FromNumericId(id);
        Assert.Equal(expectedId, result.Id);
        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedFormat, result.Format);
    }

    [Fact]
    public void FromNumericId_UnknownId_ReturnsGenericQuality()
    {
        var result = QualityMapper.FromNumericId(99, "Tidal");
        Assert.Equal("99", result.Id);
        Assert.Equal("Tidal Quality 99", result.Name);
        Assert.Equal("Unknown", result.Format);
    }

    [Fact]
    public void FromStringDescriptor_NullDescriptor_ReturnsNull()
    {
        string? descriptor = null;
        var result = QualityMapper.FromStringDescriptor(descriptor!);
        Assert.Null(result);
    }

    [Fact]
    public void FromStringDescriptor_EmptyDescriptor_ReturnsNull()
    {
        var result = QualityMapper.FromStringDescriptor(string.Empty);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("normal")]
    public void FromStringDescriptor_LowOrNormal_ReturnsMp3Low(string descriptor)
    {
        var result = QualityMapper.FromStringDescriptor(descriptor);
        Assert.Equal("mp3_128", result.Id);
        Assert.Equal(128, result.Bitrate);
    }

    [Fact]
    public void FromStringDescriptor_High_ReturnsMp3High()
    {
        var result = QualityMapper.FromStringDescriptor("high");
        Assert.Equal("mp3_320", result.Id);
        Assert.Equal(320, result.Bitrate);
    }

    [Fact]
    public void FromStringDescriptor_Lossless_ReturnsFlacCd()
    {
        var result = QualityMapper.FromStringDescriptor("lossless");
        Assert.Equal("flac_cd", result.Id);
        Assert.Equal(44100, result.SampleRate);
        Assert.Equal(16, result.BitDepth);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("hi_res")]
    [InlineData("hires")]
    public void FromStringDescriptor_HiResVariants_ReturnsFlacHiRes(string descriptor)
    {
        var result = QualityMapper.FromStringDescriptor(descriptor);
        Assert.Equal("flac_hires", result.Id);
        Assert.Equal(96000, result.SampleRate);
        Assert.Equal(24, result.BitDepth);
    }

    [Theory]
    [InlineData("studio_master")]
    [InlineData("max")]
    public void FromStringDescriptor_MaxVariants_ReturnsFlacMax(string descriptor)
    {
        var result = QualityMapper.FromStringDescriptor(descriptor);
        Assert.Equal("flac_max", result.Id);
        Assert.Equal(192000, result.SampleRate);
        Assert.Equal(24, result.BitDepth);
    }

    [Fact]
    public void FromStringDescriptor_Unknown_ReturnsGenericQuality()
    {
        var result = QualityMapper.FromStringDescriptor("DSD256", "Qobuz");
        Assert.Equal("DSD256", result.Id);
        Assert.Equal("Qobuz DSD256", result.Name);
        Assert.Equal("Unknown", result.Format);
    }

    [Fact]
    public void GetQualityDescription_NullQuality_ReturnsUnknown()
    {
        StreamingQuality? q = null;
        var result = QualityMapper.GetQualityDescription(q!);
        Assert.Equal("Unknown Quality", result);
    }

    [Fact]
    public void GetQualityDescription_LosslessHiRes_ContainsHiResLabel()
    {
        var q = StreamingQualityBuilder.CreateFlacHiRes();
        var desc = QualityMapper.GetQualityDescription(q);
        Assert.Contains("FLAC", desc);
        Assert.Contains("96.0kHz/24bit", desc);
        Assert.Contains("Hi-Res", desc);
    }

    [Fact]
    public void GetQualityDescription_LosslessCd_ContainsCdSpecs()
    {
        var q = StreamingQualityBuilder.CreateFlacCd();
        var desc = QualityMapper.GetQualityDescription(q);
        Assert.Contains("FLAC", desc);
        Assert.Contains("44.1kHz/16bit", desc);
        Assert.DoesNotContain("Hi-Res", desc);
    }

    [Fact]
    public void GetQualityDescription_LossyWithBitrate_ContainsKbps()
    {
        var q = StreamingQualityBuilder.CreateMp3320();
        var desc = QualityMapper.GetQualityDescription(q);
        Assert.Contains("MP3", desc);
        Assert.Contains("320kbps", desc);
    }

    [Fact]
    public void GetQualityDescription_NoSpecs_ReturnsName()
    {
        var q = new StreamingQualityBuilder()
            .WithFormat("")
            .WithName("Custom Quality")
            .WithBitrate(null)
            .WithSampleRate(null)
            .WithBitDepth(null)
            .Build();
        var desc = QualityMapper.GetQualityDescription(q);
        Assert.Equal("Custom Quality", desc);
    }

    [Fact]
    public void StandardQualities_Mp3Low_HasExpectedValues()
    {
        var q = QualityMapper.StandardQualities.Mp3Low;
        Assert.Equal("mp3_128", q.Id);
        Assert.Equal("MP3 128kbps", q.Name);
        Assert.Equal("MP3", q.Format);
        Assert.Equal(128, q.Bitrate);
    }

    [Fact]
    public void StandardQualities_Mp3Normal_HasExpectedValues()
    {
        var q = QualityMapper.StandardQualities.Mp3Normal;
        Assert.Equal("mp3_256", q.Id);
        Assert.Equal(256, q.Bitrate);
    }

    [Fact]
    public void StandardQualities_Mp3High_HasExpectedValues()
    {
        var q = QualityMapper.StandardQualities.Mp3High;
        Assert.Equal("mp3_320", q.Id);
        Assert.Equal(320, q.Bitrate);
    }

    [Fact]
    public void StandardQualities_FlacMax_Has192kHzAnd24Bit()
    {
        var q = QualityMapper.StandardQualities.FlacMax;
        Assert.Equal("flac_max", q.Id);
        Assert.Equal(192000, q.SampleRate);
        Assert.Equal(24, q.BitDepth);
    }
}
