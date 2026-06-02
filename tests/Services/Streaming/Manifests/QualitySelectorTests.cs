using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.Services.Streaming.Manifests;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Streaming.Manifests
{
    /// <summary>
    /// Covers <see cref="QualitySelector.SelectByBandwidthCeiling"/>: highest variant at or below the
    /// ceiling, with a lowest-variant fallback when every rendition exceeds the ceiling.
    /// </summary>
    public sealed class QualitySelectorTests
    {
        private static IReadOnlyList<StreamVariant> Variants(params int[] bandwidths)
        {
            var list = new List<StreamVariant>();
            foreach (int b in bandwidths)
            {
                list.Add(new StreamVariant(b, $"https://example/{b}.mpd", "mp4a.40.2"));
            }

            return list;
        }

        [Fact]
        public void PicksHighestAtOrBelowCeiling()
        {
            StreamVariant? chosen = QualitySelector.SelectByBandwidthCeiling(Variants(64000, 128000, 256000), 200000);

            Assert.NotNull(chosen);
            Assert.Equal(128000, chosen!.BandwidthBps);
        }

        [Fact]
        public void CeilingExactlyMatchesAVariant_IsInclusive()
        {
            StreamVariant? chosen = QualitySelector.SelectByBandwidthCeiling(Variants(64000, 128000, 256000), 128000);

            Assert.Equal(128000, chosen!.BandwidthBps);
        }

        [Fact]
        public void WhenAllExceedCeiling_FallsBackToLowest()
        {
            StreamVariant? chosen = QualitySelector.SelectByBandwidthCeiling(Variants(256000, 320000, 1129000), 100000);

            Assert.NotNull(chosen);
            Assert.Equal(256000, chosen!.BandwidthBps);
        }

        [Fact]
        public void CeilingAboveAll_PicksHighest()
        {
            StreamVariant? chosen = QualitySelector.SelectByBandwidthCeiling(Variants(64000, 128000, 256000), int.MaxValue);

            Assert.Equal(256000, chosen!.BandwidthBps);
        }

        [Fact]
        public void EmptyOrNull_ReturnsNull()
        {
            Assert.Null(QualitySelector.SelectByBandwidthCeiling(Array.Empty<StreamVariant>(), 100000));
            Assert.Null(QualitySelector.SelectByBandwidthCeiling(null!, 100000));
        }
    }
}
