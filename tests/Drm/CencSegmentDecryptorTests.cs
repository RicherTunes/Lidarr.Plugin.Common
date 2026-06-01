using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.Services.Drm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// End-to-end CENC segment decryption: a synthetic moof+mdat fragment is decrypted in place using a
    /// known content key, proving the walker + trun/senc parsers + CencDecryptor assemble correctly. The
    /// encrypted mdat payload is a NIST CTR vector, so the recovered bytes are independently known.
    /// </summary>
    public sealed class CencSegmentDecryptorTests
    {
        [Fact]
        public void DecryptSegmentInPlace_SingleCencSample_DecryptsMdatPayload()
        {
            var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
            var iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"); // 16-byte per-sample IV
            var ct = Convert.FromHexString("874d6191b620e3261bef6864990db6ce"); // NIST CTR ct block 0
            var pt = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172a"); // NIST CTR pt block 0

            // senc: version/flags(no subsamples), sample_count=1, one 16-byte IV.
            var senc = Box("senc", Concat(new byte[] { 0, 0, 0, 0 }, Be32(1), iv));
            // trun: flags = data_offset(0x1) + sample_size(0x200); count=1; data_offset=80; size=16.
            // data_offset (80) is the mdat payload start relative to the moof box start (computed below).
            var trun = Box("trun", Concat(new byte[] { 0x00, 0x00, 0x02, 0x01 }, Be32(1), Be32(80), Be32(16)));
            var moof = Box("moof", Box("traf", Concat(trun, senc)));
            var mdat = Box("mdat", ct);
            var segment = Concat(moof, mdat);

            // sanity: confirm the hardcoded data_offset matches the actual mdat payload location.
            Assert.Equal(80, moof.Length + 8);

            var tenc = new TencDefaults(IsProtected: true, PerSampleIvSize: 16, DefaultKid: new byte[16],
                CryptByteBlock: 0, SkipByteBlock: 0, DefaultConstantIv: null);

            var decrypted = CencSegmentDecryptor.DecryptSegmentInPlace(segment, key, CencProtectionScheme.Cenc, tenc);

            Assert.Equal(1, decrypted);
            Assert.Equal(pt, segment.AsSpan(80, 16).ToArray()); // mdat payload now plaintext
        }

        // Two cenc samples with DISTINCT IVs (NIST CTR counter and counter+1) prove the per-sample loop
        // resets the CTR keystream from each sample's own IV (a regression that carried state across samples
        // would pass the single-sample test but fail this one).
        [Fact]
        public void DecryptSegmentInPlace_MultipleSamples_EachUsesItsOwnIv()
        {
            var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
            var ct = Convert.FromHexString("874d6191b620e3261bef6864990db6ce9806f66b7970fdff8617187bb9fffdff");
            var pt = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
            var iv0 = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
            var iv1 = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdff00"); // counter + 1

            var segment = BuildCencSegment(new[] { (iv0, ct[0..16]), (iv1, ct[16..32]) }, out int dataOffset);
            var tenc = new TencDefaults(true, 16, new byte[16], 0, 0, null);

            var n = CencSegmentDecryptor.DecryptSegmentInPlace(segment, key, CencProtectionScheme.Cenc, tenc);

            Assert.Equal(2, n);
            Assert.Equal(pt[0..16], segment.AsSpan(dataOffset, 16).ToArray());
            Assert.Equal(pt[16..32], segment.AsSpan(dataOffset + 16, 16).ToArray());
        }

        [Fact]
        public void DecryptSegmentInPlace_NoMoof_Throws()
        {
            var data = Box("ftyp", new byte[8]);
            var tenc = new TencDefaults(true, 16, new byte[16], 0, 0, null);
            Assert.Throws<ArgumentException>(() => CencSegmentDecryptor.DecryptSegmentInPlace(data, new byte[16], CencProtectionScheme.Cenc, tenc));
        }

        [Fact]
        public void DecryptSegmentInPlace_SampleRangeExceedsSegment_Throws()
        {
            // trun claims a sample size far larger than the segment -> bounds check must reject.
            var senc = Box("senc", Concat(new byte[] { 0, 0, 0, 0 }, Be32(1), new byte[16]));
            var trun = Box("trun", Concat(new byte[] { 0x00, 0x00, 0x02, 0x01 }, Be32(1), Be32(80), Be32(100000)));
            var segment = Concat(Box("moof", Box("traf", Concat(trun, senc))), Box("mdat", new byte[16]));
            var tenc = new TencDefaults(true, 16, new byte[16], 0, 0, null);
            Assert.Throws<ArgumentException>(() => CencSegmentDecryptor.DecryptSegmentInPlace(segment, new byte[16], CencProtectionScheme.Cenc, tenc));
        }

        [Fact]
        public void DecryptSegmentInPlace_SampleSizeExceedsInt32_Throws()
        {
            var senc = Box("senc", Concat(new byte[] { 0, 0, 0, 0 }, Be32(1), new byte[16]));
            var trun = Box("trun", Concat(new byte[] { 0x00, 0x00, 0x02, 0x01 }, Be32(1), Be32(80), new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));
            var segment = Concat(Box("moof", Box("traf", Concat(trun, senc))), Box("mdat", new byte[16]));
            var tenc = new TencDefaults(true, 16, new byte[16], 0, 0, null);
            Assert.Throws<ArgumentException>(() => CencSegmentDecryptor.DecryptSegmentInPlace(segment, new byte[16], CencProtectionScheme.Cenc, tenc));
        }

        [Fact]
        public void DecryptSegmentInPlace_SencTrunCountMismatch_Throws()
        {
            var senc = Box("senc", Concat(new byte[] { 0, 0, 0, 0 }, Be32(2), new byte[16], new byte[16])); // 2 samples
            var trun = Box("trun", Concat(new byte[] { 0x00, 0x00, 0x02, 0x01 }, Be32(1), Be32(80), Be32(16))); // 1 sample
            var segment = Concat(Box("moof", Box("traf", Concat(trun, senc))), Box("mdat", new byte[16]));
            var tenc = new TencDefaults(true, 16, new byte[16], 0, 0, null);
            Assert.Throws<ArgumentException>(() => CencSegmentDecryptor.DecryptSegmentInPlace(segment, new byte[16], CencProtectionScheme.Cenc, tenc));
        }

        // Builds a moof+mdat cenc segment (no subsamples) from per-sample (iv, ciphertext), computing the
        // moof-relative data_offset. Box sizes don't depend on the data_offset value, so build with a
        // placeholder, measure the moof, then rebuild with the real offset.
        private static byte[] BuildCencSegment(IReadOnlyList<(byte[] iv, byte[] data)> samples, out int dataOffset)
        {
            var sencInner = new List<byte>();
            sencInner.AddRange(new byte[] { 0, 0, 0, 0 }); // version/flags: no subsamples
            sencInner.AddRange(Be32(samples.Count));
            foreach (var s in samples) sencInner.AddRange(s.iv);
            var senc = Box("senc", sencInner.ToArray());

            byte[] BuildTrun(int doff)
            {
                var t = new List<byte>();
                t.AddRange(new byte[] { 0x00, 0x00, 0x02, 0x01 }); // data-offset + sample-size
                t.AddRange(Be32(samples.Count));
                t.AddRange(Be32(doff));
                foreach (var s in samples) t.AddRange(Be32(s.data.Length));
                return Box("trun", t.ToArray());
            }

            int moofLen = Box("moof", Box("traf", Concat(BuildTrun(0), senc))).Length;
            dataOffset = moofLen + 8; // mdat payload start relative to moof
            var moof = Box("moof", Box("traf", Concat(BuildTrun(dataOffset), senc)));
            var mdat = Box("mdat", Concat(samples.Select(s => s.data).ToArray()));
            return Concat(moof, mdat);
        }

        private static byte[] Box(string type, byte[] payload)
            => Concat(Be32(8 + payload.Length), Ascii(type), payload);

        private static byte[] Be32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

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
