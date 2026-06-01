using System;
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
