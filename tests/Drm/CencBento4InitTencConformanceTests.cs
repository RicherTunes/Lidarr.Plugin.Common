using System;
using Lidarr.Plugin.Common.Services.Drm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// GOLD-STANDARD conformance for the INIT-segment box parsing (the box-walking half of the CENC pipeline,
    /// complementing the segment-decryption conformance vs mp4decrypt). Mp4BoxWalker must descend through the
    /// FULL real nesting a reference muxer emits — <c>moov/trak/mdia/minf/stbl/stsd/enca/sinf/schi/tenc</c> —
    /// to find the <c>tenc</c> box, and CencBoxParser.ParseTenc must read its real values. The init segment
    /// below is the actual ftyp+moov produced by Bento4 <c>mp4encrypt --method MPEG-CENC</c>
    /// (KID 9eb4050de44b4802932e27d75083e266, per-sample IV size 16); mp4dump confirms default_isProtected=1,
    /// default_Per_Sample_IV_Size=16, scheme_type=cenc. Embedded (base64) so CI needs no Bento4.
    /// </summary>
    public sealed class CencBento4InitTencConformanceTests
    {
        [Fact]
        public void ParseTenc_FromRealBento4InitSegment_MatchesMp4dump()
        {
            var init = Convert.FromBase64String(InitBase64);

            // The walker must reach tenc through the real stsd->enca->sinf->schi nesting (ChildSkip on
            // stsd/enca; sinf/schi as containers). A regression here would silently lose the IV size + KID.
            var tencBox = Mp4BoxWalker.FindFirst(init, "tenc");
            Assert.NotNull(tencBox);

            var tenc = CencBoxParser.ParseTenc(init.AsSpan(tencBox!.PayloadOffset, tencBox.PayloadLength));

            Assert.True(tenc.IsProtected);
            Assert.Equal(16, tenc.PerSampleIvSize);
            Assert.Equal(Convert.FromHexString("9eb4050de44b4802932e27d75083e266"), tenc.DefaultKid);
            // cenc carries no crypt:skip pattern (that is cbcs/cens).
            Assert.Equal(0, tenc.CryptByteBlock);
            Assert.Equal(0, tenc.SkipByteBlock);
        }

        private const string InitBase64 =
            "AAAAJGZ0eXBpc29tAAACAGlzb21pc28ybXA0MWlzbzVpc282AAACo21vb3YAAABsbXZoZAAAAADmQ+tG5kPrRgAAA+gAAADIAAEAAAEAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAP////8AAAH3dHJhawAAAFx0a2hkAAAABwAAAAAAAAAAAAAAAQAAAAAAAADIAAAAAAAAAAAAAAABAQAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAABk21kaWEAAAAgbWRoZAAAAAAAAAAAAAAAAAAArEQAAAAAVcQAAAAAADVoZGxyAAAAAAAAAABzb3VuAAAAAAAAAAAAAAAAQmVudG80IFNvdW5kIEhhbmRsZXIAAAABNm1pbmYAAAAQc21oZAAAAAAAAAAAAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADHVybCAAAAABAAAA+nN0YmwAAACuc3RzZAAAAAAAAAABAAAAnmVuY2EAAAAAAAAAAQAAAAAAAAAAAAEAEAAAAACsRAAAAAAAKmVzZHMAAAAAAxwAAAAEFEAVAAAAAACCwQAAgsEFBRIIVuUABgECAAAAUHNpbmYAAAAMZnJtYW1wNGEAAAAUc2NobQAAAABjZW5jAAEAAAAAAChzY2hpAAAAIHRlbmMAAAAAAAABEJ60BQ3kS0gCky4n11CD4mYAAAAQc3R0cwAAAAAAAAAAAAAAEHN0c2MAAAAAAAAAAAAAABRzdHN6AAAAAAAAAAAAAAAAAAAAEHN0Y28AAAAAAAAAAAAAADhtdmV4AAAAEG1laGQAAAAAAAAAyAAAACB0cmV4AAAAAAAAAAEAAAABAAAAAAAAAAAAAAAA";
    }
}
