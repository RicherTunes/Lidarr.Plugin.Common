using System;

namespace Lidarr.Plugin.Common.Services.Drm
{
    /// <summary>
    /// Decrypts a CENC-protected fragmented-MP4 media segment (moof + mdat) in place, given the content key
    /// and the track-encryption defaults (from the init segment's 'tenc'). Ties together the box walker, the
    /// 'trun' (sample byte ranges in mdat) and 'senc' (per-sample IVs + subsample maps) parsers, and
    /// <see cref="CencDecryptor"/>. This is the integration point a plugin calls once it has a content key
    /// (the key is an INPUT — acquired separately by the CDM/license layer).
    /// </summary>
    public static class CencSegmentDecryptor
    {
        /// <summary>
        /// Decrypts every protected sample of the segment in place. Returns the number of samples decrypted.
        /// </summary>
        /// <param name="segment">The full moof+mdat media segment, mutated in place.</param>
        /// <param name="key">The 16-byte content key.</param>
        /// <param name="scheme">CTR ('cenc') or CBC ('cbcs') — from the init segment's 'schm'.</param>
        /// <param name="tenc">Track-encryption defaults (per-sample IV size, pattern, constant IV).</param>
        public static int DecryptSegmentInPlace(
            Span<byte> segment, ReadOnlySpan<byte> key, CencProtectionScheme scheme, TencDefaults tenc)
        {
            if (tenc is null)
            {
                throw new ArgumentNullException(nameof(tenc));
            }

            ReadOnlySpan<byte> ro = segment;

            var moof = Mp4BoxWalker.FindFirst(ro, "moof")
                ?? throw new ArgumentException("Segment has no 'moof' box.", nameof(segment));
            var sencBox = Mp4BoxWalker.FindFirst(ro, "senc")
                ?? throw new ArgumentException("Segment has no 'senc' box.", nameof(segment));
            var trunBox = Mp4BoxWalker.FindFirst(ro, "trun")
                ?? throw new ArgumentException("Segment has no 'trun' box.", nameof(segment));

            var senc = CencBoxParser.ParseSenc(ro.Slice(sencBox.PayloadOffset, sencBox.PayloadLength), tenc.PerSampleIvSize);
            var trun = CencBoxParser.ParseTrun(ro.Slice(trunBox.PayloadOffset, trunBox.PayloadLength));

            if (trun.DataOffset is null)
            {
                throw new ArgumentException("trun has no data_offset; cannot locate samples in mdat.", nameof(segment));
            }

            if (trun.SampleSizes.Count != senc.Count)
            {
                throw new ArgumentException(
                    $"sample-count mismatch: senc has {senc.Count}, trun has {trun.SampleSizes.Count}.", nameof(segment));
            }

            // data_offset is relative to the moof box start (the DASH/CMAF default-base-is-moof convention).
            int sampleStart = moof.Offset + trun.DataOffset.Value;

            using var decryptor = new CencDecryptor(key, scheme);
            for (int i = 0; i < senc.Count; i++)
            {
                long size = trun.SampleSizes[i];
                if (sampleStart < 0 || (long)sampleStart + size > segment.Length)
                {
                    throw new ArgumentException($"sample {i} byte range [{sampleStart}, +{size}) exceeds the segment ({segment.Length}).", nameof(segment));
                }

                ReadOnlySpan<byte> iv = tenc.PerSampleIvSize == 0
                    ? tenc.DefaultConstantIv ?? throw new ArgumentException("cbcs requires a default constant IV in tenc.", nameof(tenc))
                    : senc[i].Iv;

                var subsamples = senc[i].Subsamples;
                ReadOnlySpan<CencSubsample> subsSpan = subsamples.Count == 0 ? default : ToArray(subsamples);

                decryptor.DecryptSampleInPlace(
                    segment.Slice(sampleStart, (int)size), iv, subsSpan, tenc.CryptByteBlock, tenc.SkipByteBlock);

                sampleStart += (int)size;
            }

            return senc.Count;
        }

        private static CencSubsample[] ToArray(System.Collections.Generic.IReadOnlyList<CencSubsample> list)
        {
            var arr = new CencSubsample[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                arr[i] = list[i];
            }

            return arr;
        }
    }
}
