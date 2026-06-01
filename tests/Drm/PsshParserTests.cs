using System;
using Lidarr.Plugin.Common.Services.Drm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Parses a full ISO-BMFF 'pssh' box (the form base64-encoded in a DASH <c>&lt;cenc:pssh&gt;</c> element or
    /// carried in an init segment). Verified against hand-built boxes whose fields are known by construction.
    /// Handles v0 (no KIDs) and v1 (explicit KID list) — the v1/KID case the in-tree amazon parser mishandles.
    /// </summary>
    public sealed class PsshParserTests
    {
        private static readonly byte[] Widevine = Convert.FromHexString("edef8ba979d64acea3c827dcd51d21ed");

        [Fact]
        public void Parse_V0_NoKids_ReadsSystemIdAndData()
        {
            var data = Convert.FromHexString("deadbeef");
            var box = Concat(
                Be32(8 + 4 + 16 + 4 + data.Length), // box size
                Ascii("pssh"),
                new byte[] { 0x00, 0x00, 0x00, 0x00 }, // version 0 + flags
                Widevine,
                Be32(data.Length),
                data);

            var pssh = PsshParser.Parse(box);

            Assert.Equal(Widevine, pssh.SystemId);
            Assert.Empty(pssh.KeyIds);
            Assert.Equal(data, pssh.Data);
        }

        [Fact]
        public void Parse_V1_WithKids_ReadsKidList()
        {
            var kid0 = Convert.FromHexString("00112233445566778899aabbccddeeff");
            var kid1 = Convert.FromHexString("ffeeddccbbaa99887766554433221100");
            var data = Convert.FromHexString("cafe");
            var box = Concat(
                Be32(8 + 4 + 16 + 4 + 32 + 4 + data.Length),
                Ascii("pssh"),
                new byte[] { 0x01, 0x00, 0x00, 0x00 }, // version 1 + flags
                Widevine,
                Be32(2),                                // KID_count
                kid0, kid1,
                Be32(data.Length),
                data);

            var pssh = PsshParser.Parse(box);

            Assert.Equal(Widevine, pssh.SystemId);
            Assert.Equal(2, pssh.KeyIds.Count);
            Assert.Equal(kid0, pssh.KeyIds[0]);
            Assert.Equal(kid1, pssh.KeyIds[1]);
            Assert.Equal(data, pssh.Data);
        }

        [Fact]
        public void Parse_WrongBoxType_Throws()
        {
            var box = Concat(Be32(16), Ascii("moov"), new byte[8]);
            Assert.Throws<ArgumentException>(() => PsshParser.Parse(box));
        }

        [Fact]
        public void Parse_DataSizeExceedsBox_Throws()
        {
            var box = Concat(
                Be32(8 + 4 + 16 + 4),
                Ascii("pssh"),
                new byte[] { 0x00, 0x00, 0x00, 0x00 },
                Widevine,
                Be32(1000)); // claims 1000 data bytes that aren't there

            Assert.Throws<ArgumentException>(() => PsshParser.Parse(box));
        }

        // v1 pssh carrying KIDs but no data (DataSize 0) — legal; Data must be empty, the KID still read.
        [Fact]
        public void Parse_V1_ZeroDataSize_ReadsKidWithEmptyData()
        {
            var kid = Convert.FromHexString("00112233445566778899aabbccddeeff");
            var box = Concat(
                Be32(8 + 4 + 16 + 4 + 16 + 4),
                Ascii("pssh"),
                new byte[] { 0x01, 0x00, 0x00, 0x00 },
                Widevine,
                Be32(1),
                kid,
                Be32(0)); // DataSize 0

            var pssh = PsshParser.Parse(box);

            Assert.Single(pssh.KeyIds);
            Assert.Equal(kid, pssh.KeyIds[0]);
            Assert.Empty(pssh.Data);
        }

        // Regression: a huge KID_count with no KID bytes must throw, not loop hundreds of millions of times.
        [Fact]
        public void Parse_V1_HugeKidCount_ThrowsFast()
        {
            var box = Concat(
                Be32(8 + 4 + 16 + 4),
                Ascii("pssh"),
                new byte[] { 0x01, 0x00, 0x00, 0x00 },
                Widevine,
                Be32(0x10000000)); // 268M KIDs, none present

            Assert.Throws<ArgumentException>(() => PsshParser.Parse(box));
        }

        private static byte[] Be32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
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
