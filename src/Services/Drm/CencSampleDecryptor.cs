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
            IReadOnlyList<CencSubsample>? subsamples = null,
            int cryptByteBlock = 0,
            int skipByteBlock = 0)
        {
            if (key.Length != 16)
            {
                throw new ArgumentException(
                    $"CENC content key must be 16 bytes (AES-128), got {key.Length}.", nameof(key));
            }

            if (scheme is not (CencProtectionScheme.Cenc or CencProtectionScheme.Cbcs))
            {
                throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Unsupported CENC protection scheme.");
            }

            if (cryptByteBlock < 0 || skipByteBlock < 0)
            {
                throw new ArgumentException("crypt/skip byte-block counts must be non-negative.", nameof(cryptByteBlock));
            }

            // The crypt:skip striping pattern applies to 'cbcs' only (and 'cens', which is unsupported);
            // plain 'cenc' must be unpatterned.
            if (scheme == CencProtectionScheme.Cenc && (cryptByteBlock != 0 || skipByteBlock != 0))
            {
                throw new ArgumentException("'cenc' does not support a crypt:skip pattern; use 'cbcs'.", nameof(scheme));
            }

            // 'cenc' IVs are 8 or 16 bytes (8 → low 8 bytes are the block counter); 'cbcs' uses a 16-byte IV.
            bool ivOk = scheme == CencProtectionScheme.Cenc ? iv.Length is 8 or 16 : iv.Length == 16;
            if (!ivOk)
            {
                throw new ArgumentException(
                    $"CENC IV must be {(scheme == CencProtectionScheme.Cbcs ? "16" : "8 or 16")} bytes for {scheme}, got {iv.Length}.",
                    nameof(iv));
            }

            if (subsamples is not null)
            {
                ValidateSubsamples(subsamples, sample.Length);
            }

            var output = sample.ToArray();

            using var aes = Aes.Create();
            var keyCopy = key.ToArray();
            try
            {
                aes.Key = keyCopy;
                DecryptInto(aes, output, iv, scheme, subsamples, cryptByteBlock, skipByteBlock);
            }
            finally
            {
                // Scrub the transient managed copy of the content key (the Aes-internal key schedule is
                // framework-owned and not portably wipeable, but this removes the obvious heap copy).
                CryptographicOperations.ZeroMemory(keyCopy);
            }

            return output;
        }

        private static void DecryptInto(
            Aes aes,
            byte[] output,
            ReadOnlySpan<byte> iv,
            CencProtectionScheme scheme,
            IReadOnlyList<CencSubsample>? subsamples,
            int cryptByteBlock,
            int skipByteBlock)
        {
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
                // blocks; any trailing partial block in a run is left in the clear. When a crypt:skip pattern
                // is set, only the first cryptByteBlock blocks of each (crypt+skip) group are encrypted and
                // CBC chaining continues across the skipped blocks from crypt group to crypt group.
                bool patterned = skipByteBlock > 0 && cryptByteBlock > 0;
                ForEachProtectedRun(output.Length, subsamples, (offset, length) =>
                {
                    if (patterned)
                    {
                        CbcDecryptPattern(aes, ivBlock, output, offset, length, cryptByteBlock, skipByteBlock);
                    }
                    else
                    {
                        CbcDecryptFullBlocks(aes, ivBlock, output, offset, length);
                    }
                });
            }
        }

        /// <summary>
        /// 'cbcs' striped CBC decryption of a protected run: decrypt the first <paramref name="cryptBlocks"/>
        /// whole blocks of each (crypt+skip) block group, leave the next <paramref name="skipBlocks"/> blocks
        /// clear, and continue CBC chaining across the skipped blocks (the last ciphertext block of one crypt
        /// group is the IV of the next). A trailing partial block (run not a multiple of 16) is left clear.
        /// </summary>
        private static void CbcDecryptPattern(
            Aes aes, byte[] iv, byte[] data, int offset, int length, int cryptBlocks, int skipBlocks)
        {
            int totalBlocks = length / 16;
            var chainIv = (byte[])iv.Clone();
            int pos = offset;
            int done = 0;

            while (done < totalBlocks)
            {
                int crypt = Math.Min(cryptBlocks, totalBlocks - done);
                int cryptBytes = crypt * 16;

                var cipher = new byte[cryptBytes];
                Array.Copy(data, pos, cipher, 0, cryptBytes);
                var plain = aes.DecryptCbc(cipher, chainIv, PaddingMode.None);
                Array.Copy(plain, 0, data, pos, cryptBytes);

                // Chain into the next crypt group from this group's last ciphertext block.
                Array.Copy(cipher, cryptBytes - 16, chainIv, 0, 16);

                pos += cryptBytes;
                done += crypt;

                int skip = Math.Min(skipBlocks, totalBlocks - done);
                pos += skip * 16;
                done += skip;
            }
        }

        /// <summary>
        /// Rejects a malformed/malicious subsample map before any crypto or allocation runs: negative run
        /// lengths, and a clear+protected total that overflows or exceeds the sample length. Closes the
        /// OOB-read, silent-corruption, and over-allocation (OOM) vectors a crafted MP4 could trigger.
        /// </summary>
        private static void ValidateSubsamples(IReadOnlyList<CencSubsample> subsamples, int totalLength)
        {
            long pos = 0;
            foreach (var ss in subsamples)
            {
                if (ss.ClearBytes < 0 || ss.ProtectedBytes < 0)
                {
                    throw new ArgumentException("CENC subsample lengths must be non-negative.", nameof(subsamples));
                }

                pos += (long)ss.ClearBytes + ss.ProtectedBytes; // long avoids int overflow on the running sum
                if (pos > totalLength)
                {
                    throw new ArgumentException(
                        $"CENC subsample map ({pos} bytes) exceeds sample length ({totalLength}).", nameof(subsamples));
                }
            }
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
