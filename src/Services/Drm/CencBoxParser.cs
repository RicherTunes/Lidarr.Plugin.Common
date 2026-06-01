using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Drm
{
    /// <summary>
    /// Per-sample encryption info from a 'senc' box: the sample's IV and (when present) its subsample map.
    /// </summary>
    public sealed record CencSampleEncryptionInfo(byte[] Iv, IReadOnlyList<CencSubsample> Subsamples);

    /// <summary>
    /// The track-encryption defaults from an ISO/IEC 23001-7 'tenc' box: whether the track is protected, the
    /// per-sample IV size, the default KID, the cbcs crypt:skip pattern, and (when the per-sample IV size is
    /// zero) the default constant IV.
    /// </summary>
    public sealed record TencDefaults(
        bool IsProtected,
        int PerSampleIvSize,
        byte[] DefaultKid,
        int CryptByteBlock,
        int SkipByteBlock,
        byte[]? DefaultConstantIv);

    /// <summary>
    /// Parses the CENC MP4 boxes ('tenc' track-encryption defaults; 'senc' per-sample IVs + subsample maps)
    /// that supply the IVs/subsample maps and scheme parameters <see cref="CencDecryptor"/> consumes. Shared
    /// across every CENC plugin (Widevine, FairPlay-cbcs, PlayReady).
    /// </summary>
    public static class CencBoxParser
    {
        /// <summary>
        /// Parses a 'tenc' box payload (the FullBox content beginning at the version byte).
        /// </summary>
        public static TencDefaults ParseTenc(ReadOnlySpan<byte> payload)
        {
            // version(1) flags(3) reserved(1) [reserved|pattern](1) isProtected(1) ivSize(1) KID(16) = 24
            if (payload.Length < 24)
            {
                throw new ArgumentException($"tenc payload too short ({payload.Length} bytes, need >= 24).", nameof(payload));
            }

            byte version = payload[0];
            byte patternByte = payload[5];
            int crypt = 0, skip = 0;
            if (version >= 1)
            {
                crypt = patternByte >> 4;
                skip = patternByte & 0x0F;
            }

            bool isProtected = payload[6] == 1;
            int ivSize = payload[7];
            var kid = payload.Slice(8, 16).ToArray();

            byte[]? constantIv = null;
            if (isProtected && ivSize == 0)
            {
                if (payload.Length < 25)
                {
                    throw new ArgumentException("tenc declares a constant IV but the box is truncated.", nameof(payload));
                }

                int constIvSize = payload[24];
                if (payload.Length < 25 + constIvSize)
                {
                    throw new ArgumentException(
                        $"tenc constant IV ({constIvSize} bytes) exceeds the box ({payload.Length - 25} available).", nameof(payload));
                }

                constantIv = payload.Slice(25, constIvSize).ToArray();
            }

            return new TencDefaults(isProtected, ivSize, kid, crypt, skip, constantIv);
        }

        /// <summary>
        /// Parses a 'senc' box payload (FullBox content from the version byte) into per-sample IVs +
        /// subsample maps. <paramref name="perSampleIvSize"/> comes from the matching 'tenc'
        /// (<see cref="TencDefaults.PerSampleIvSize"/>); 0 means the constant IV is used and per-sample IVs
        /// are absent. Every field is bounds-checked against the box so a truncated/crafted box throws rather
        /// than over-reads or over-allocates.
        /// </summary>
        public static IReadOnlyList<CencSampleEncryptionInfo> ParseSenc(ReadOnlySpan<byte> payload, int perSampleIvSize)
        {
            if (perSampleIvSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(perSampleIvSize));
            }

            if (payload.Length < 8)
            {
                throw new ArgumentException($"senc payload too short ({payload.Length} bytes, need >= 8).", nameof(payload));
            }

            uint flags = (uint)((payload[1] << 16) | (payload[2] << 8) | payload[3]);
            bool hasSubsamples = (flags & 0x000002) != 0;

            int pos = 4;
            long sampleCount = ReadUInt32(payload, pos);
            pos += 4;

            // Reject a sample_count the box can't hold BEFORE looping. Each sample consumes at least the
            // per-sample IV (+2 bytes for subsample_count when subsampled). The dangerous case: iv_size==0
            // AND no subsamples means a sample consumes ZERO bytes, so a huge sample_count would loop ~4.3
            // billion times allocating empty entries (an 8-byte hostile senc → OOM/hang) — guard it.
            long minBytesPerSample = perSampleIvSize + (hasSubsamples ? 2 : 0);
            if (minBytesPerSample == 0)
            {
                if (sampleCount != 0)
                {
                    throw new ArgumentException(
                        "senc with per-sample IV size 0 and no subsamples carries no per-sample data; sample_count must be 0.", nameof(payload));
                }
            }
            else if (sampleCount > (payload.Length - pos) / minBytesPerSample)
            {
                throw new ArgumentException(
                    $"senc sample_count ({sampleCount}) exceeds the box capacity.", nameof(payload));
            }

            // Do NOT pre-size from the attacker-controlled sample_count; the guard above bounds it to the
            // box's real byte budget, so a bogus count throws instead of OOM-ing.
            var samples = new List<CencSampleEncryptionInfo>();
            for (long s = 0; s < sampleCount; s++)
            {
                Require(payload, pos, perSampleIvSize, "per-sample IV");
                var iv = payload.Slice(pos, perSampleIvSize).ToArray();
                pos += perSampleIvSize;

                IReadOnlyList<CencSubsample> subs = Array.Empty<CencSubsample>();
                if (hasSubsamples)
                {
                    Require(payload, pos, 2, "subsample_count");
                    int subCount = (payload[pos] << 8) | payload[pos + 1];
                    pos += 2;

                    var list = new List<CencSubsample>(subCount);
                    for (int i = 0; i < subCount; i++)
                    {
                        Require(payload, pos, 6, "subsample entry");
                        int clear = (payload[pos] << 8) | payload[pos + 1];
                        long prot = ReadUInt32(payload, pos + 2);
                        pos += 6;
                        if (prot > int.MaxValue)
                        {
                            throw new ArgumentException("senc subsample protected length exceeds Int32.", nameof(payload));
                        }

                        list.Add(new CencSubsample(clear, (int)prot));
                    }

                    subs = list;
                }

                samples.Add(new CencSampleEncryptionInfo(iv, subs));
            }

            return samples;
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> b, int pos)
            => (uint)((b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3]);

        private static void Require(ReadOnlySpan<byte> payload, int pos, int need, string what)
        {
            // long math so a large pos can't wrap negative and slip past the check (build is unchecked).
            if ((long)pos + need > payload.Length)
            {
                throw new ArgumentException($"senc box truncated reading {what} (need {need} bytes at {pos}, have {payload.Length}).", nameof(payload));
            }
        }
    }
}
