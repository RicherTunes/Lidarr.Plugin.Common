using System;
using System.Collections.Generic;

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

            // tfhd (optional) supplies the sample-data anchor and the default sample size when trun omits sizes.
            var tfhdBox = Mp4BoxWalker.FindFirst(ro, "tfhd");
            var tfhd = tfhdBox is null ? null : CencBoxParser.ParseTfhd(ro.Slice(tfhdBox.PayloadOffset, tfhdBox.PayloadLength));

            // senc.Count is bounded by the senc box capacity guard — use it as the authoritative sample count
            // (a huge trun sample_count is rejected here rather than driving an allocation).
            if (trun.SampleCount != senc.Count)
            {
                throw new ArgumentException(
                    $"sample-count mismatch: senc has {senc.Count}, trun declares {trun.SampleCount}.", nameof(segment));
            }

            // Per-sample sizes: explicit trun sizes, else the tfhd default_sample_size for every sample.
            IReadOnlyList<long> sizes;
            if (trun.SampleSizes.Count == senc.Count)
            {
                sizes = trun.SampleSizes;
            }
            else if (trun.SampleSizes.Count == 0 && tfhd?.DefaultSampleSize is uint defaultSize)
            {
                var filled = new long[senc.Count];
                Array.Fill(filled, defaultSize);
                sizes = filled;
            }
            else
            {
                throw new ArgumentException(
                    "trun omits per-sample sizes and tfhd has no default_sample_size.", nameof(segment));
            }

            // Sample-data anchor: an explicit base_data_offset (unless default-base-is-moof overrides it),
            // otherwise the enclosing moof start (the CMAF default). data_offset is added to the anchor.
            int anchor = (tfhd is { BaseDataOffset: long bdo, DefaultBaseIsMoof: false })
                ? checked((int)bdo)
                : moof.Offset;
            int sampleStart = anchor + (trun.DataOffset ?? 0);

            using var decryptor = new CencDecryptor(key, scheme);
            for (int i = 0; i < senc.Count; i++)
            {
                long size = sizes[i];
                if (size > int.MaxValue)
                {
                    throw new ArgumentException($"sample {i} size {size} exceeds Int32.", nameof(segment));
                }

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
