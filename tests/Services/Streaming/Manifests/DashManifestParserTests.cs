using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lidarr.Plugin.Common.Services.Streaming.Manifests;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Streaming.Manifests
{
    /// <summary>
    /// Golden-file tests for <see cref="DashManifestParser"/>. These assert EXACT segment counts,
    /// ordering, <c>$Number$</c>-substituted URLs, and the parsed variant list against realistic MPD
    /// fixtures. They are the regression guard for the two correctness invariants the shared parser
    /// must get right (and which the two pre-consolidation Tidal parsers disagreed on):
    /// <list type="number">
    ///   <item><c>SegmentTimeline S@r</c> is the count of ADDITIONAL repeats, so <c>&lt;S d r=N/&gt;</c>
    ///   yields <c>N + 1</c> segments.</item>
    ///   <item><c>SegmentTemplate@startNumber</c> (default 1) is the <c>$Number$</c> of the FIRST media
    ///   segment and increments thereafter (off-by-one guard).</item>
    /// </list>
    /// </summary>
    public sealed class DashManifestParserTests
    {
        private readonly DashManifestParser _parser = new();

        private static string LoadFixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "manifests", name);
            return File.ReadAllText(path);
        }

        [Theory]
        [InlineData("application/dash+xml")]
        [InlineData("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\">")]
        [InlineData("<?xml version=\"1.0\"?>\n<MPD>")]
        public void CanParse_RecognizesDashByMimeOrContent(string input)
        {
            Assert.True(_parser.CanParse(input));
        }

        [Theory]
        [InlineData("application/vnd.apple.mpegurl")]
        [InlineData("#EXTM3U")]
        [InlineData("")]
        [InlineData(null)]
        public void CanParse_RejectsNonDash(string? input)
        {
            Assert.False(_parser.CanParse(input!));
        }

        // --- Fixture 1: SegmentTimeline with an r-repeat + non-1 startNumber (the off-by-one guard) ---

        [Fact]
        public void SegmentTimeline_RRepeatYieldsNPlusOne_AndHonorsStartNumber()
        {
            StreamManifest manifest = _parser.Parse(LoadFixture("tidal_segment_timeline.mpd"), "https://manifest.example.com/track-42/manifest.mpd");

            // <S d=44100 r=2/> => 3 segments, <S d=22050 r=0/> => 1 segment => 4 media + 1 init = 5 total.
            Assert.Equal(5, manifest.Segments.Count);

            // Index 0 is the init segment (no $Number$ substitution), resolved against the <BaseURL>.
            Assert.Equal("https://cdn.example.com/audio/track-42/init_flac_lossless.mp4", manifest.Segments[0].Url);
            Assert.Equal(0, manifest.Segments[0].Index);

            // Media segments are numbered from startNumber=5: 5,6,7,8 (zero-padded to width 6).
            string[] expectedMedia =
            {
                "https://cdn.example.com/audio/track-42/seg_flac_lossless_000005.m4s",
                "https://cdn.example.com/audio/track-42/seg_flac_lossless_000006.m4s",
                "https://cdn.example.com/audio/track-42/seg_flac_lossless_000007.m4s",
                "https://cdn.example.com/audio/track-42/seg_flac_lossless_000008.m4s",
            };
            Assert.Equal(expectedMedia, manifest.Segments.Skip(1).Select(s => s.Url).ToArray());

            // Ordering / indices are contiguous 0..4.
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, manifest.Segments.Select(s => s.Index).ToArray());

            // Per-segment duration derived from d/timescale: first three are 44100/44100 = 1.0s, last is 0.5s.
            Assert.Equal(new double?[] { null, 1.0, 1.0, 1.0, 0.5 }, manifest.Segments.Select(s => s.DurationSeconds).ToArray());
        }

        [Fact]
        public void SegmentTimeline_SingleRepresentation_ProducesOneVariant_WithCodecAndBandwidth()
        {
            StreamManifest manifest = _parser.Parse(LoadFixture("tidal_segment_timeline.mpd"), "https://manifest.example.com/track-42/manifest.mpd");

            StreamVariant variant = Assert.Single(manifest.Variants);
            Assert.Equal(1129000, variant.BandwidthBps);
            Assert.Equal("flac", variant.Codec);
        }

        [Fact]
        public void SegmentTimeline_SurfacesWidevinePsshAndKid_AsOpaqueData_AndFlagsEncrypted()
        {
            StreamManifest manifest = _parser.Parse(LoadFixture("tidal_segment_timeline.mpd"), "https://manifest.example.com/track-42/manifest.mpd");

            Assert.True(manifest.IsEncrypted);
            Assert.Equal("9eb4050d-e44b-4802-932e-27d75083e266", manifest.KeyId);
            Assert.Equal(
                "AAAAW3Bzc2gAAAAA7e+LqXnWSs6jyCfc1R0h7QAAADsIARIQnrQFDeRLSAKTLifXUIPiZhoNd2lkZXZpbmVfdGVzdCIQbWFuaWZlc3RfdGVzdF9pZA==",
                manifest.Pssh);
            Assert.Equal("flac", manifest.Codec);
            Assert.Equal(".m4a", manifest.FileExtension);
        }

        // --- Fixture 2: multi-Representation, duration-based SegmentTemplate (no timeline) ---

        [Fact]
        public void MultiRepresentation_ProducesVariantPerRepresentation_InOrder()
        {
            StreamManifest manifest = _parser.Parse(LoadFixture("amazon_multi_representation.mpd"), "https://music.example.com/dash/x/manifest.mpd");

            Assert.Equal(3, manifest.Variants.Count);
            Assert.Equal(new[] { 64000, 128000, 256000 }, manifest.Variants.Select(v => v.BandwidthBps).ToArray());
            Assert.All(manifest.Variants, v => Assert.Equal("mp4a.40.2", v.Codec));
        }

        [Fact]
        public void DurationBased_SegmentCountIsCeilOfTotalOverSegmentDuration_NumberedFromStartNumber()
        {
            StreamManifest manifest = _parser.Parse(LoadFixture("amazon_multi_representation.mpd"), "https://music.example.com/dash/x/manifest.mpd");

            // Segment duration = duration/timescale = 48000/48000 = 1.0s. Total = PT20S.
            // => ceil(20 / 1.0) = 20 media segments + 1 init = 21 total.
            Assert.Equal(21, manifest.Segments.Count);

            // Segments come from the HIGHEST-bandwidth Representation ("hi"), numbered from startNumber=1.
            Assert.Equal("https://music.example.com/dash/x/hi/init.mp4", manifest.Segments[0].Url);
            Assert.Equal("https://music.example.com/dash/x/hi/seg-1-256000.m4s", manifest.Segments[1].Url);
            Assert.Equal("https://music.example.com/dash/x/hi/seg-2-256000.m4s", manifest.Segments[2].Url);
            Assert.Equal("https://music.example.com/dash/x/hi/seg-20-256000.m4s", manifest.Segments[20].Url);

            // Each 1.0s segment carries its derived duration.
            Assert.All(manifest.Segments.Skip(1), s => Assert.Equal(1.0, s.DurationSeconds));

            // Indices contiguous, init first.
            Assert.Equal(Enumerable.Range(0, 21).ToArray(), manifest.Segments.Select(s => s.Index).ToArray());
        }

        [Fact]
        public void MultiRepresentation_NoContentProtection_IsNotEncrypted()
        {
            StreamManifest manifest = _parser.Parse(LoadFixture("amazon_multi_representation.mpd"), "https://music.example.com/dash/x/manifest.mpd");

            Assert.False(manifest.IsEncrypted);
            Assert.Null(manifest.Pssh);
            Assert.Null(manifest.KeyId);
        }
    }
}
