using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Lidarr.Plugin.Common.Services.Drm
{
    /// <summary>
    /// CENC (Common Encryption, ISO/IEC 23001-7) protection scheme for a protected sample.
    /// </summary>
    public enum CencProtectionScheme
    {
        /// <summary>'cenc' — AES-128 in CTR mode; the IV is the 16-byte initial counter.</summary>
        Cenc,

        /// <summary>'cbcs' — AES-128 in CBC mode; the IV resets at the start of each protected subsample.</summary>
        Cbcs,
    }

    /// <summary>
    /// One CENC subsample: a run of <see cref="ClearBytes"/> cleartext bytes immediately followed by a run
    /// of <see cref="ProtectedBytes"/> encrypted bytes. A sample's subsamples partition it left-to-right.
    /// </summary>
    public readonly record struct CencSubsample(int ClearBytes, int ProtectedBytes);

    /// <summary>
    /// Decrypts a single CENC-protected media sample given the content key, the sample's IV, the protection
    /// scheme, and (optionally) its subsample map. This is the shared, CDM-agnostic crypto primitive every
    /// Widevine/PlayReady/FairPlay-cbcs plugin needs — it does the actual AES, not key acquisition.
    /// </summary>
    public static class CencSampleDecryptor
    {
        /// <summary>
        /// Decrypts <paramref name="sample"/> in place semantics (returns a new array).
        /// </summary>
        /// <param name="sample">The full protected sample bytes.</param>
        /// <param name="key">The 16-byte AES-128 content key.</param>
        /// <param name="iv">The sample IV: 8 or 16 bytes (8-byte IVs are right-padded with zeros to 16).</param>
        /// <param name="scheme">CTR ('cenc') or CBC ('cbcs').</param>
        /// <param name="subsamples">
        /// The subsample map. When <c>null</c>, the whole sample is treated as a single fully-encrypted run.
        /// </param>
        public static byte[] DecryptSample(
            ReadOnlySpan<byte> sample,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> iv,
            CencProtectionScheme scheme,
            IReadOnlyList<CencSubsample>? subsamples = null)
        {
            var output = sample.ToArray();

            using var aes = Aes.Create();
            aes.Key = key.ToArray();

            var ivBlock = new byte[16];
            iv.CopyTo(ivBlock);

            if (scheme == CencProtectionScheme.Cenc)
            {
                // One continuous CTR keystream is consumed by the protected bytes only; clear bytes are
                // skipped without advancing the keystream, and the stream continues across subsamples.
                var keystream = new CtrKeystream(aes, ivBlock);
                ForEachProtectedRun(output.Length, subsamples, (offset, length) =>
                    keystream.XorInto(output, offset, length));
            }
            else
            {
                // 'cbcs' resets the IV at the start of each protected run and decrypts only whole 16-byte
                // blocks; any trailing partial block in a run is left in the clear.
                ForEachProtectedRun(output.Length, subsamples, (offset, length) =>
                    CbcDecryptFullBlocks(aes, ivBlock, output, offset, length));
            }

            return output;
        }

        /// <summary>
        /// Invokes <paramref name="onProtected"/> with the (offset, length) of each protected byte run.
        /// With no subsample map the whole sample is one protected run; otherwise each subsample contributes
        /// its <see cref="CencSubsample.ClearBytes"/> (skipped) then <see cref="CencSubsample.ProtectedBytes"/>.
        /// </summary>
        private static void ForEachProtectedRun(
            int totalLength, IReadOnlyList<CencSubsample>? subsamples, Action<int, int> onProtected)
        {
            if (subsamples is null)
            {
                onProtected(0, totalLength);
                return;
            }

            int pos = 0;
            foreach (var ss in subsamples)
            {
                pos += ss.ClearBytes;
                if (ss.ProtectedBytes > 0)
                {
                    onProtected(pos, ss.ProtectedBytes);
                    pos += ss.ProtectedBytes;
                }
            }
        }

        /// <summary>
        /// AES-CBC-decrypts the whole-block portion of <paramref name="data"/>[offset..offset+length) in
        /// place with no padding. Any trailing bytes that do not fill a 16-byte block are left untouched.
        /// </summary>
        private static void CbcDecryptFullBlocks(Aes aes, byte[] iv, byte[] data, int offset, int length)
        {
            int blocks = length / 16;
            if (blocks == 0)
            {
                return;
            }

            int blockBytes = blocks * 16;
            var cipher = new byte[blockBytes];
            Array.Copy(data, offset, cipher, 0, blockBytes);
            var plain = aes.DecryptCbc(cipher, iv, PaddingMode.None);
            Array.Copy(plain, 0, data, offset, blockBytes);
        }

        /// <summary>
        /// A resumable AES-CTR keystream. <see cref="XorInto"/> consumes keystream bytes exactly equal to the
        /// number of bytes XORed, so interleaved clear gaps (skipped via separate calls) keep the keystream
        /// continuous across protected runs — the ISO 23001-7 'cenc' rule.
        /// </summary>
        private sealed class CtrKeystream
        {
            private readonly Aes _aes;
            private readonly byte[] _counter;
            private readonly byte[] _block = new byte[16];
            private int _blockPos = 16; // force a fresh block on first use

            public CtrKeystream(Aes aes, byte[] counter)
            {
                _aes = aes;
                _counter = counter;
            }

            public void XorInto(byte[] data, int offset, int length)
            {
                for (int i = 0; i < length; i++)
                {
                    if (_blockPos == 16)
                    {
                        _aes.EncryptEcb(_counter, _block, PaddingMode.None);
                        IncrementCounter(_counter);
                        _blockPos = 0;
                    }

                    data[offset + i] ^= _block[_blockPos++];
                }
            }
        }

        private static void IncrementCounter(byte[] counter)
        {
            for (int i = counter.Length - 1; i >= 0; i--)
            {
                if (++counter[i] != 0)
                {
                    break;
                }
            }
        }
    }
}
