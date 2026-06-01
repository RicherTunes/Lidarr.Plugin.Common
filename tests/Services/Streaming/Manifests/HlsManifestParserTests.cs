using System;
using System.IO;
using System.Linq;
using Lidarr.Plugin.Common.Services.Streaming.Manifests;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Streaming.Manifests
{
    /// <summary>
    /// Golden-file tests for <see cref="HlsManifestParser"/>. They assert EXACT variant extraction from a
    /// master <c>.m3u8</c> (bandwidth/codec/URL, in file order) and EXACT segment extraction from a media
    /// <c>.m3u8</c> (ordered URLs, <c>#EXTINF</c> durations, relative + absolute URL resolution), plus the
    /// <c>#EXT-X-KEY</c> encryption flag and opaque <c>KEYID</c>. They are the regression guard for the
    /// inline HLS walk that applemusicarr will migrate onto this shared parser.
    /// </summary>
    public sealed class HlsManifestParserTests
    {
        private readonly HlsManifestParser _parser = new();

        private static string LoadFixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "manifests", name);
            return File.ReadAllText(path);
        }

        [Theory]
        [InlineData("application/vnd.apple.mpegurl")]
        [InlineData("application/x-mpegurl")]
        [InlineData("#EXTM3U")]
        [InlineData("#EXTM3U\n#EXT-X-VERSION:6")]
        [InlineData("https://cdn.example.com/track/master.m3u8")]
        [InlineData("https://cdn.example.com/track/master.m3u8?token=abc")]
        public void CanParse_RecognizesHlsByMimeContentOrExtension(string input)
        {
            Assert.True(_parser.CanParse(input));
        }

        [Theory]
        [InlineData("application/dash+xml")]
        [InlineData("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\">")]
        [InlineData("")]
        [InlineData(null)]
        public void CanParse_RejectsNonHls(string? input)
        {
            Assert.False(_parser.CanParse(input!));
        }

        // --- Fixture 1: master playlist with multiple #EXT-X-STREAM-INF variants ---

        [Fact]
        public void Master_ProducesVariantPerStreamInf_InFileOrder_WithBandwidthAndCodec()
        {
            StreamManifest manifest = _parser.Parse(
                LoadFixture("apple_master.m3u8"),
                "https://cdn.example.com/audio/track-7/master.m3u8");

            Assert.Equal(3, manifest.Variants.Count);

            // Bandwidths preserved in file order (NOT sorted).
            Assert.Equal(new[] { 64000, 256000, 1411000 }, manifest.Variants.Select(v => v.BandwidthBps).ToArray());

            // CODECS is a quoted list; the FIRST entry is the primary codec ("alac" for the lossless rendition).
            Assert.Equal(new[] { "mp4a.40.2", "mp4a.40.2", "alac" }, manifest.Variants.Select(v => v.Codec).ToArray());
        }

        [Fact]
        public void Master_ResolvesRelativeVariantUrls_AgainstBaseUrl()
        {
            StreamManifest manifest = _parser.Parse(
                LoadFixture("apple_master.m3u8"),
                "https://cdn.example.com/audio/track-7/master.m3u8");

            Assert.Equal(
                new[]
                {
                    "https://cdn.example.com/audio/track-7/aac-lc/64/prog_index.m3u8",
                    "https://cdn.example.com/audio/track-7/aac-lc/256/prog_index.m3u8",
                    "https://cdn.example.com/audio/track-7/alac/1411/prog_index.m3u8",
                },
                manifest.Variants.Select(v => v.Url).ToArray());
        }

        [Fact]
        public void Master_HasNoSegments_AndIsNotEncrypted()
        {
            StreamManifest manifest = _parser.Parse(
                LoadFixture("apple_master.m3u8"),
                "https://cdn.example.com/audio/track-7/master.m3u8");

            // A master playlist exposes variants only; the caller selects one and re-parses it.
            Assert.Empty(manifest.Segments);
            Assert.False(manifest.IsEncrypted);
            Assert.Null(manifest.KeyId);
            Assert.Null(manifest.Pssh);

            // Manifest-level codec reflects the highest-bandwidth rendition (alac).
            Assert.Equal("alac", manifest.Codec);
            Assert.Equal(".m4a", manifest.FileExtension);
        }

        [Fact]
        public void Master_VariantsAreSelectableByQualitySelector()
        {
            StreamManifest manifest = _parser.Parse(
                LoadFixture("apple_master.m3u8"),
                "https://cdn.example.com/audio/track-7/master.m3u8");

            // The caller flow: parse master -> select a variant -> re-parse that variant.
            StreamVariant? chosen = QualitySelector.SelectByBandwidthCeiling(manifest.Variants, 300000);
            Assert.NotNull(chosen);
            Assert.Equal(256000, chosen!.BandwidthBps);
            Assert.Equal("https://cdn.example.com/audio/track-7/aac-lc/256/prog_index.m3u8", chosen.Url);
        }

        // --- Fixture 2: media (variant) playlist with #EXTINF segments + #EXT-X-KEY ---

        [Fact]
        public void Media_ExtractsOrderedSegments_WithExtInfDurations()
        {
            StreamManifest manifest = _parser.Parse(
                LoadFixture("apple_variant_encrypted.m3u8"),
                "https://cdn.example.com/audio/track-7/alac/1411/prog_index.m3u8");

            // Three #EXTINF segments; #EXT-X-MAP / #EXT-X-KEY are NOT segments.
            Assert.Equal(3, manifest.Segments.Count);

            // Indices contiguous from 0, in file order.
            Assert.Equal(new[] { 0, 1, 2 }, manifest.Segments.Select(s => s.Index).ToArray());

            // Durations from #EXTINF:<seconds>,<title>.
            Assert.Equal(new double?[] { 6.0, 6.0, 4.25 }, manifest.Segments.Select(s => s.DurationSeconds).ToArray());
        }

        [Fact]
        public void Media_ResolvesRelativeAndAbsoluteSegmentUrls()
        {
            StreamManifest manifest = _parser.Parse(
                LoadFixture("apple_variant_encrypted.m3u8"),
                "https://cdn.example.com/audio/track-7/alac/1411/prog_index.m3u8");

            Assert.Equal(
                new[]
                {
                    // Relative URIs resolved against the variant playlist URL's directory.
                    "https://cdn.example.com/audio/track-7/alac/1411/seg-0.m4s",
                    "https://cdn.example.com/audio/track-7/alac/1411/seg-1.m4s",
                    // Already-absolute URI passed through unchanged.
                    "https://cdn.example.com/audio/track-7/alac/1411/seg-2.m4s",
                },
                manifest.Segments.Select(s => s.Url).ToArray());
        }

        [Fact]
        public void Media_ExtKey_FlagsEncrypted_AndSurfacesKeyIdAsOpaqueData()
        {
            StreamManifest manifest = _parser.Parse(
                LoadFixture("apple_variant_encrypted.m3u8"),
                "https://cdn.example.com/audio/track-7/alac/1411/prog_index.m3u8");

            Assert.True(manifest.IsEncrypted);

            // KEYID is surfaced verbatim as opaque data (no parsing/decoding of the hex KID).
            Assert.Equal("0x9eb4050de44b4802932e27d75083e266", manifest.KeyId);

            // HLS carries no PSSH (that is a DASH/CENC construct).
            Assert.Null(manifest.Pssh);

            // Apple audio segments are fragmented MP4 -> .m4a.
            Assert.Equal(".m4a", manifest.FileExtension);
        }

        [Fact]
        public void Media_ExtKeyMethodNone_IsNotEncrypted()
        {
            const string playlist =
                "#EXTM3U\n" +
                "#EXT-X-VERSION:3\n" +
                "#EXT-X-KEY:METHOD=NONE\n" +
                "#EXTINF:6.0,\n" +
                "seg-0.aac\n" +
                "#EXT-X-ENDLIST\n";

            StreamManifest manifest = _parser.Parse(playlist, "https://cdn.example.com/a/index.m3u8");

            Assert.False(manifest.IsEncrypted);
            Assert.Null(manifest.KeyId);
            Assert.Single(manifest.Segments);

            // .aac segment URL drives the extension sniff.
            Assert.Equal(".aac", manifest.FileExtension);
            Assert.Equal("https://cdn.example.com/a/seg-0.aac", manifest.Segments[0].Url);
        }

        [Fact]
        public void Parse_EmptyContent_Throws()
        {
            Assert.Throws<ArgumentException>(() => _parser.Parse("", "https://x/y.m3u8"));
        }
    }
}
