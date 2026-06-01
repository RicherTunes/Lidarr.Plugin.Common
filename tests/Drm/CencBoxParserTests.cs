using System;
using Lidarr.Plugin.Common.Services.Drm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Parses the ISO/IEC 23001-7 CENC MP4 boxes ('tenc' track-encryption defaults; 'senc' per-sample
    /// IVs + subsample maps) that feed <see cref="CencDecryptor"/>. Verified against hand-built boxes whose
    /// fields are known by construction (the box layout is the spec, not the impl).
    /// </summary>
    public sealed class CencBoxParserTests
    {
        // tenc v0 (cenc): reserved, reserved, isProtected=1, iv_size=8, 16-byte KID, no constant IV.
        [Fact]
        public void ParseTenc_V0_Cenc_ReadsDefaults()
        {
            var kid = Convert.FromHexString("00112233445566778899aabbccddeeff");
            var payload = Concat(
                new byte[] { 0x00 },             // version 0
                new byte[] { 0x00, 0x00, 0x00 }, // flags
                new byte[] { 0x00 },             // reserved
                new byte[] { 0x00 },             // reserved (version 0)
                new byte[] { 0x01 },             // default_isProtected
                new byte[] { 0x08 },             // default_Per_Sample_IV_Size
                kid);

            var tenc = CencBoxParser.ParseTenc(payload);

            Assert.True(tenc.IsProtected);
            Assert.Equal(8, tenc.PerSampleIvSize);
            Assert.Equal(kid, tenc.DefaultKid);
            Assert.Equal(0, tenc.CryptByteBlock);
            Assert.Equal(0, tenc.SkipByteBlock);
            Assert.Null(tenc.DefaultConstantIv);
        }

        // tenc v1 (cbcs): pattern byte = crypt<<4 | skip = 1:9; iv_size=0 so a 16-byte constant IV follows.
        [Fact]
        public void ParseTenc_V1_Cbcs_ReadsPatternAndConstantIv()
        {
            var kid = Convert.FromHexString("0f0e0d0c0b0a09080706050403020100");
            var constantIv = Convert.FromHexString("aabbccddeeff00112233445566778899");
            var payload = Concat(
                new byte[] { 0x01 },             // version 1
                new byte[] { 0x00, 0x00, 0x00 }, // flags
                new byte[] { 0x00 },             // reserved
                new byte[] { (1 << 4) | 9 },     // crypt_byte_block=1, skip_byte_block=9
                new byte[] { 0x01 },             // default_isProtected
                new byte[] { 0x00 },             // default_Per_Sample_IV_Size = 0 => constant IV present
                kid,
                new byte[] { 0x10 },             // default_constant_IV_size = 16
                constantIv);

            var tenc = CencBoxParser.ParseTenc(payload);

            Assert.True(tenc.IsProtected);
            Assert.Equal(0, tenc.PerSampleIvSize);
            Assert.Equal(kid, tenc.DefaultKid);
            Assert.Equal(1, tenc.CryptByteBlock);
            Assert.Equal(9, tenc.SkipByteBlock);
            Assert.Equal(constantIv, tenc.DefaultConstantIv);
        }

        // senc with subsamples (flags & 0x000002), iv_size=8, two samples.
        [Fact]
        public void ParseSenc_WithSubsamples_ReadsPerSampleIvsAndMaps()
        {
            var payload = Concat(
                new byte[] { 0x00 },             // version
                new byte[] { 0x00, 0x00, 0x02 }, // flags: uses subsamples
                new byte[] { 0x00, 0x00, 0x00, 0x02 }, // sample_count = 2
                // sample 0
                new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 }, // IV (8)
                new byte[] { 0x00, 0x02 },                                     // subsample_count = 2
                new byte[] { 0x00, 0x05, 0x00, 0x00, 0x00, 0x64 },             // clear=5, protected=100
                new byte[] { 0x00, 0x00, 0x00, 0x00, 0x07, 0xD0 },             // clear=0, protected=2000
                // sample 1
                new byte[] { 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18 }, // IV (8)
                new byte[] { 0x00, 0x01 },                                     // subsample_count = 1
                new byte[] { 0x00, 0x0A, 0x00, 0x00, 0x01, 0xF4 });            // clear=10, protected=500

            var samples = CencBoxParser.ParseSenc(payload, perSampleIvSize: 8);

            Assert.Equal(2, samples.Count);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 }, samples[0].Iv);
            Assert.Equal(new[] { new CencSubsample(5, 100), new CencSubsample(0, 2000) }, samples[0].Subsamples);
            Assert.Equal(new byte[] { 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18 }, samples[1].Iv);
            Assert.Equal(new[] { new CencSubsample(10, 500) }, samples[1].Subsamples);
        }

        // senc without the subsamples flag: each sample is just a 16-byte IV, no subsample map.
        [Fact]
        public void ParseSenc_NoSubsamples_ReadsIvsOnly()
        {
            var iv = Convert.FromHexString("0102030405060708090a0b0c0d0e0f10");
            var payload = Concat(
                new byte[] { 0x00 },             // version
                new byte[] { 0x00, 0x00, 0x00 }, // flags: no subsamples
                new byte[] { 0x00, 0x00, 0x00, 0x01 }, // sample_count = 1
                iv);

            var samples = CencBoxParser.ParseSenc(payload, perSampleIvSize: 16);

            Assert.Single(samples);
            Assert.Equal(iv, samples[0].Iv);
            Assert.Empty(samples[0].Subsamples);
        }

        // A truncated box (sample_count claims more samples than the bytes hold) must throw, not over-read.
        [Fact]
        public void ParseSenc_Truncated_Throws()
        {
            var payload = Concat(
                new byte[] { 0x00 },
                new byte[] { 0x00, 0x00, 0x00 },
                new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, // sample_count = huge
                new byte[] { 0x01, 0x02 });            // only 2 bytes of IV data

            Assert.Throws<ArgumentException>(() => CencBoxParser.ParseSenc(payload, perSampleIvSize: 8));
        }

        // Regression: a crafted senc with per-sample IV size 0, no subsamples, and a huge sample_count would
        // loop ~4.3 billion times allocating empty entries (OOM/hang). Must throw fast instead.
        [Fact]
        public void ParseSenc_ZeroIvNoSubsamples_HugeCount_ThrowsFast()
        {
            var payload = new byte[] { 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF }; // count = 4.29e9
            Assert.Throws<ArgumentException>(() => CencBoxParser.ParseSenc(payload, perSampleIvSize: 0));
        }

        [Fact]
        public void ParseSenc_EmptySampleCount_ReturnsEmpty()
        {
            var payload = new byte[] { 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00 }; // subsamples flag, count 0
            var samples = CencBoxParser.ParseSenc(payload, perSampleIvSize: 8);
            Assert.Empty(samples);
        }

        // cbcs can use a crypt:skip pattern AND a per-sample IV (size 16) with no constant IV — verify the
        // constant IV is only read when iv_size==0, not merely because version>=1.
        [Fact]
        public void ParseTenc_V1_Cbcs_PatternWithPerSampleIv_NoConstantIv()
        {
            var kid = Convert.FromHexString("00112233445566778899aabbccddeeff");
            var payload = Concat(
                new byte[] { 0x01 },
                new byte[] { 0x00, 0x00, 0x00 },
                new byte[] { 0x00 },
                new byte[] { (1 << 4) | 9 },     // crypt=1, skip=9
                new byte[] { 0x01 },             // isProtected
                new byte[] { 0x10 },             // per_sample_IV_size = 16 (NOT 0) => no constant IV
                kid);

            var tenc = CencBoxParser.ParseTenc(payload);

            Assert.Equal(16, tenc.PerSampleIvSize);
            Assert.Equal(1, tenc.CryptByteBlock);
            Assert.Equal(9, tenc.SkipByteBlock);
            Assert.Null(tenc.DefaultConstantIv);
        }

        // trun with data-offset (0x1) + sample-size (0x200) flags, 2 samples.
        [Fact]
        public void ParseTrun_DataOffsetAndSampleSizes_ReadsBoth()
        {
            var payload = Concat(
                new byte[] { 0x00 },             // version
                new byte[] { 0x00, 0x02, 0x01 }, // flags: data-offset + sample-size
                new byte[] { 0x00, 0x00, 0x00, 0x02 }, // sample_count = 2
                new byte[] { 0x00, 0x00, 0x00, 0x64 }, // data_offset = 100
                new byte[] { 0x00, 0x00, 0x04, 0x00 }, // sample 0 size = 1024
                new byte[] { 0x00, 0x00, 0x08, 0x00 }); // sample 1 size = 2048

            var trun = CencBoxParser.ParseTrun(payload);

            Assert.Equal(100, trun.DataOffset);
            Assert.Equal(new long[] { 1024, 2048 }, trun.SampleSizes);
        }

        // All per-sample fields present (duration+size+flags+cto) — the size must be read from the right
        // offset within each 16-byte per-sample record, and first_sample_flags (0x4) skipped.
        [Fact]
        public void ParseTrun_AllPerSampleFields_ExtractsSizeFromCorrectOffset()
        {
            var payload = Concat(
                new byte[] { 0x00 },
                new byte[] { 0x00, 0x0F, 0x05 }, // flags: data-offset(1) + first-sample-flags(4) + dur(100)+size(200)+flags(400)+cto(800)
                new byte[] { 0x00, 0x00, 0x00, 0x01 }, // sample_count = 1
                new byte[] { 0x00, 0x00, 0x00, 0x10 }, // data_offset = 16
                new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, // first_sample_flags
                new byte[] { 0x00, 0x00, 0x00, 0x05 }, // sample duration
                new byte[] { 0x00, 0x00, 0x0A, 0x00 }, // sample size = 2560
                new byte[] { 0x00, 0x00, 0x00, 0x00 }, // sample flags
                new byte[] { 0x00, 0x00, 0x00, 0x00 }); // composition time offset

            var trun = CencBoxParser.ParseTrun(payload);

            Assert.Equal(16, trun.DataOffset);
            Assert.Equal(new long[] { 2560 }, trun.SampleSizes);
        }

        [Fact]
        public void ParseTrun_NoSizeFlag_ReturnsEmptySizes()
        {
            var payload = Concat(
                new byte[] { 0x00 },
                new byte[] { 0x00, 0x00, 0x01 }, // flags: data-offset only
                new byte[] { 0x00, 0x00, 0x00, 0x02 }, // sample_count = 2
                new byte[] { 0x00, 0x00, 0x00, 0x64 }); // data_offset

            var trun = CencBoxParser.ParseTrun(payload);

            Assert.Equal(100, trun.DataOffset);
            Assert.Empty(trun.SampleSizes);
        }

        [Fact]
        public void ParseTrun_Truncated_Throws()
        {
            var payload = Concat(
                new byte[] { 0x00 },
                new byte[] { 0x00, 0x02, 0x00 }, // sample-size flag
                new byte[] { 0x00, 0x00, 0x00, 0x03 }, // claims 3 samples
                new byte[] { 0x00, 0x00, 0x04, 0x00 }); // only 1 size present

            Assert.Throws<ArgumentException>(() => CencBoxParser.ParseTrun(payload));
        }

        // tfhd with base-data-offset (0x1) + default-sample-size (0x10) + default-base-is-moof (0x20000).
        [Fact]
        public void ParseTfhd_BaseOffsetDefaultSizeAndMoofBase_ReadsAll()
        {
            var payload = Concat(
                new byte[] { 0x00 },             // version
                new byte[] { 0x02, 0x00, 0x11 }, // tf_flags = 0x020011
                new byte[] { 0x00, 0x00, 0x00, 0x01 }, // track_ID = 1
                new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00 }, // base_data_offset = 4096
                new byte[] { 0x00, 0x00, 0x02, 0x00 }); // default_sample_size = 512

            var tfhd = CencBoxParser.ParseTfhd(payload);

            Assert.Equal(1u, tfhd.TrackId);
            Assert.Equal(4096L, tfhd.BaseDataOffset);
            Assert.Equal(512u, tfhd.DefaultSampleSize);
            Assert.True(tfhd.DefaultBaseIsMoof);
        }

        [Fact]
        public void ParseTfhd_TrackIdOnly_WithMoofBase()
        {
            var payload = Concat(
                new byte[] { 0x00 },
                new byte[] { 0x02, 0x00, 0x00 }, // only default-base-is-moof
                new byte[] { 0x00, 0x00, 0x00, 0x02 }); // track_ID = 2

            var tfhd = CencBoxParser.ParseTfhd(payload);

            Assert.Equal(2u, tfhd.TrackId);
            Assert.Null(tfhd.BaseDataOffset);
            Assert.Null(tfhd.DefaultSampleSize);
            Assert.True(tfhd.DefaultBaseIsMoof);
        }

        // default-sample-size present, but NO base-offset and NO default-base-is-moof.
        [Fact]
        public void ParseTfhd_DefaultSizeNoMoofBase()
        {
            var payload = Concat(
                new byte[] { 0x00 },
                new byte[] { 0x00, 0x00, 0x10 }, // default-sample-size only
                new byte[] { 0x00, 0x00, 0x00, 0x03 }, // track_ID = 3
                new byte[] { 0x00, 0x00, 0x01, 0x00 }); // default_sample_size = 256

            var tfhd = CencBoxParser.ParseTfhd(payload);

            Assert.Equal(3u, tfhd.TrackId);
            Assert.Null(tfhd.BaseDataOffset);
            Assert.Equal(256u, tfhd.DefaultSampleSize);
            Assert.False(tfhd.DefaultBaseIsMoof);
        }

        [Fact]
        public void ParseTfhd_Truncated_Throws()
        {
            // base-offset flag set but the 8 base_data_offset bytes are missing.
            var payload = Concat(
                new byte[] { 0x00 },
                new byte[] { 0x00, 0x00, 0x01 }, // base-data-offset-present
                new byte[] { 0x00, 0x00, 0x00, 0x01 }); // track_ID only

            Assert.Throws<ArgumentException>(() => CencBoxParser.ParseTfhd(payload));
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var result = new byte[n];
            int pos = 0;
            foreach (var p in parts) { p.CopyTo(result, pos); pos += p.Length; }
            return result;
        }
    }
}
