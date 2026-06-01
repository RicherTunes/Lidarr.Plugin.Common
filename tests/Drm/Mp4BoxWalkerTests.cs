using System;
using Lidarr.Plugin.Common.Services.Drm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Walks the ISO-BMFF box tree of an init/media segment to locate the CENC boxes (moof/traf/senc,
    /// moov/.../tenc, pssh) that feed the parsers. Verified against hand-built box trees.
    /// </summary>
    public sealed class Mp4BoxWalkerTests
    {
        [Fact]
        public void ReadBoxes_TopLevel_ReadsTypesOffsetsSizes()
        {
            // ftyp (size 16) then moof (size 12)
            var data = Concat(
                Box("ftyp", new byte[8]),
                Box("moof", new byte[4]));

            var boxes = Mp4BoxWalker.ReadBoxes(data);

            Assert.Equal(2, boxes.Count);
            Assert.Equal("ftyp", boxes[0].Type);
            Assert.Equal(0, boxes[0].Offset);
            Assert.Equal(8, boxes[0].HeaderLength);
            Assert.Equal(16, boxes[0].Size);
            Assert.Equal("moof", boxes[1].Type);
            Assert.Equal(16, boxes[1].Offset);
            Assert.Equal(12, boxes[1].Size);
        }

        [Fact]
        public void ReadBoxes_Largesize_Reads64BitSize()
        {
            // size==1 => 64-bit largesize follows the type; header is 16 bytes.
            var box = Concat(
                Be32(1),
                Ascii("mdat"),
                Be64(24),
                new byte[8]);

            var boxes = Mp4BoxWalker.ReadBoxes(box);

            Assert.Single(boxes);
            Assert.Equal("mdat", boxes[0].Type);
            Assert.Equal(16, boxes[0].HeaderLength);
            Assert.Equal(24, boxes[0].Size);
            Assert.Equal(8, boxes[0].PayloadLength);
        }

        [Fact]
        public void ReadBoxes_SizeZero_ExtendsToEnd()
        {
            // size==0 => box runs to the end of the buffer.
            var box = Concat(Be32(0), Ascii("mdat"), new byte[20]);

            var boxes = Mp4BoxWalker.ReadBoxes(box);

            Assert.Single(boxes);
            Assert.Equal(28, boxes[0].Size);
            Assert.Equal(20, boxes[0].PayloadLength);
        }

        [Fact]
        public void ReadBoxes_BoxExtendsPastBuffer_Throws()
        {
            var box = Concat(Be32(100), Ascii("moov"), new byte[4]); // claims 100, only 12 present
            Assert.Throws<ArgumentException>(() => Mp4BoxWalker.ReadBoxes(box));
        }

        [Fact]
        public void FindFirst_DescendsContainers_FindsSencInMoofTraf()
        {
            var senc = Box("senc", new byte[8]);
            var traf = Box("traf", senc);
            var moof = Box("moof", traf);

            var found = Mp4BoxWalker.FindFirst(moof, "senc");

            Assert.NotNull(found);
            Assert.Equal("senc", found!.Type);
            Assert.Equal(16, found.Offset);        // moof header (8) + traf header (8)
            Assert.Equal(16, found.Size);
            Assert.Equal(24, found.PayloadOffset);
        }

        [Fact]
        public void FindFirst_NotPresent_ReturnsNull()
        {
            var data = Box("moof", Box("traf", new byte[4]));
            Assert.Null(Mp4BoxWalker.FindFirst(data, "senc"));
        }

        // ---- helpers: build well-formed boxes ----
        private static byte[] Box(string type, byte[] payload)
            => Concat(Be32(8 + payload.Length), Ascii(type), payload);

        private static byte[] Be32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] Be64(long v) => new[]
        {
            (byte)(v >> 56), (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32),
            (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v,
        };
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
