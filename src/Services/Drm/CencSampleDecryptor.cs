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
        /// <summary>'cenc' — AES-128 in CTR mode; the IV is the 16-byte initial counter (or 8-byte, padded).</summary>
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
    /// A reusable, per-track CENC decryptor: the AES key schedule is computed once at construction and the
    /// cached <see cref="Aes"/> is reused for every sample of the track. This is the CDM-agnostic crypto
    /// primitive (the actual AES, not key acquisition). Construct one per content key and call
    /// <see cref="DecryptSampleInPlace"/> per sample with that sample's IV + subsample map.
    /// <para>
    /// <b>Not thread-safe.</b> A single instance shares one mutable <see cref="Aes"/>; do not call
    /// <see cref="DecryptSampleInPlace"/> concurrently on the same instance. Decrypt a track's samples
    /// sequentially (the natural order — 'cenc' CTR continuity is itself a serial contract), or construct
    /// one <see cref="CencDecryptor"/> per thread/track.
    /// </para>
    /// </summary>
    public sealed class CencDecryptor : IDisposable
    {
        private readonly Aes _aes;
        private readonly CencProtectionScheme _scheme;
        private readonly byte[] _keyCopy;
        private bool _disposed;

        /// <param name="key">The 16-byte AES-128 content key.</param>
        /// <param name="scheme">CTR ('cenc') or CBC ('cbcs').</param>
        public CencDecryptor(ReadOnlySpan<byte> key, CencProtectionScheme scheme)
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

            _scheme = scheme;
            _aes = Aes.Create();
            _keyCopy = key.ToArray();
            _aes.Key = _keyCopy; // AES key schedule computed once; reused for every sample.
        }

        /// <summary>
        /// Decrypts one protected sample <b>in place</b>.
        /// </summary>
        /// <param name="sample">The sample bytes, mutated in place.</param>
        /// <param name="iv">The sample IV: 8 or 16 bytes for 'cenc' (8 → low 8 are the block counter); 16 for 'cbcs'.</param>
        /// <param name="subsamples">
        /// The subsample map. When empty, the whole sample is one protected run.
        /// </param>
        /// <param name="cryptByteBlock">'cbcs' striping: encrypted 16-byte blocks per (crypt+skip) group (0 = full).</param>
        /// <param name="skipByteBlock">'cbcs' striping: clear 16-byte blocks per group (0 = full).</param>
        public void DecryptSampleInPlace(
            Span<byte> sample,
            ReadOnlySpan<byte> iv,
            ReadOnlySpan<CencSubsample> subsamples = default,
            int cryptByteBlock = 0,
            int skipByteBlock = 0)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            bool ivOk = _scheme == CencProtectionScheme.Cenc ? iv.Length is 8 or 16 : iv.Length == 16;
            if (!ivOk)
            {
                throw new ArgumentException(
                    $"CENC IV must be {(_scheme == CencProtectionScheme.Cbcs ? "16" : "8 or 16")} bytes for {_scheme}, got {iv.Length}.",
                    nameof(iv));
            }

            if (cryptByteBlock < 0 || skipByteBlock < 0)
            {
                throw new ArgumentException("crypt/skip byte-block counts must be non-negative.", nameof(cryptByteBlock));
            }

            // The crypt:skip striping pattern applies to 'cbcs' only; plain 'cenc' must be unpatterned.
            if (_scheme == CencProtectionScheme.Cenc && (cryptByteBlock != 0 || skipByteBlock != 0))
            {
                throw new ArgumentException("'cenc' does not support a crypt:skip pattern; use 'cbcs'.", nameof(cryptByteBlock));
            }

            ValidateSubsamples(subsamples, sample.Length);

            Span<byte> ivBlock = stackalloc byte[16];
            ivBlock.Clear();
            iv.CopyTo(ivBlock);

            if (_scheme == CencProtectionScheme.Cenc)
            {
                // One continuous CTR keystream consumed by protected bytes only; clear bytes skipped without
                // advancing it; continuous across subsamples. In place — no per-sample copy.
                var keystream = new CtrKeystream(_aes, ivBlock);
                if (subsamples.IsEmpty)
                {
                    keystream.XorInto(sample, 0, sample.Length);
                }
                else
                {
                    int pos = 0;
                    foreach (var ss in subsamples)
                    {
                        pos += ss.ClearBytes;
                        if (ss.ProtectedBytes > 0)
                        {
                            keystream.XorInto(sample, pos, ss.ProtectedBytes);
                            pos += ss.ProtectedBytes;
                        }
                    }
                }
            }
            else
            {
                bool patterned = skipByteBlock > 0 && cryptByteBlock > 0;
                if (subsamples.IsEmpty)
                {
                    CbcRun(sample, ivBlock, 0, sample.Length, patterned, cryptByteBlock, skipByteBlock);
                }
                else
                {
                    int pos = 0;
                    foreach (var ss in subsamples)
                    {
                        pos += ss.ClearBytes;
                        if (ss.ProtectedBytes > 0)
                        {
                            CbcRun(sample, ivBlock, pos, ss.ProtectedBytes, patterned, cryptByteBlock, skipByteBlock);
                            pos += ss.ProtectedBytes;
                        }
                    }
                }
            }
        }

        private void CbcRun(Span<byte> data, ReadOnlySpan<byte> iv, int offset, int length, bool patterned, int cryptBlocks, int skipBlocks)
        {
            if (patterned)
            {
                CbcDecryptPattern(data, iv, offset, length, cryptBlocks, skipBlocks);
            }
            else
            {
                CbcDecryptFullBlocks(data, iv, offset, length);
            }
        }

        /// <summary>
        /// AES-CBC-decrypts the whole-block portion of a run in place with no padding. A trailing partial
        /// block is left clear. The ciphertext is copied to a scratch buffer first so the in-place CBC read
        /// of the previous ciphertext block is not clobbered by the plaintext write.
        /// </summary>
        private void CbcDecryptFullBlocks(Span<byte> data, ReadOnlySpan<byte> iv, int offset, int length)
        {
            int blocks = length / 16;
            if (blocks == 0)
            {
                return;
            }

            int blockBytes = blocks * 16;
            Span<byte> dst = data.Slice(offset, blockBytes);
            var cipher = dst.ToArray();
            _aes.DecryptCbc(cipher, iv, dst, PaddingMode.None);
        }

        /// <summary>
        /// 'cbcs' striped CBC decryption of a run: decrypt the first <paramref name="cryptBlocks"/> whole
        /// blocks of each (crypt+skip) group, leave <paramref name="skipBlocks"/> blocks clear, and continue
        /// CBC chaining across the skipped blocks (last crypt ciphertext block is the next group's IV). A
        /// trailing partial block is left clear.
        /// </summary>
        private void CbcDecryptPattern(Span<byte> data, ReadOnlySpan<byte> iv, int offset, int length, int cryptBlocks, int skipBlocks)
        {
            int totalBlocks = length / 16;
            Span<byte> chainIv = stackalloc byte[16];
            iv.Slice(0, 16).CopyTo(chainIv);
            int pos = offset;
            int done = 0;

            while (done < totalBlocks)
            {
                int crypt = Math.Min(cryptBlocks, totalBlocks - done);
                int cryptBytes = crypt * 16;
                Span<byte> dst = data.Slice(pos, cryptBytes);
                var cipher = dst.ToArray();
                _aes.DecryptCbc(cipher, chainIv, dst, PaddingMode.None);

                // Chain into the next crypt group from this group's last ciphertext block.
                cipher.AsSpan(cryptBytes - 16, 16).CopyTo(chainIv);

                pos += cryptBytes;
                done += crypt;

                int skip = Math.Min(skipBlocks, totalBlocks - done);
                pos += skip * 16;
                done += skip;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_keyCopy);
        }

        /// <summary>
        /// Rejects a malformed/malicious subsample map before any crypto or allocation: negative run lengths,
        /// and a clear+protected total that overflows or exceeds the sample length. Closes the OOB-read,
        /// silent-corruption, and over-allocation (OOM) vectors a crafted MP4 could trigger.
        /// </summary>
        private static void ValidateSubsamples(ReadOnlySpan<CencSubsample> subsamples, int totalLength)
        {
            long pos = 0;
            foreach (var ss in subsamples)
            {
                if (ss.ClearBytes < 0 || ss.ProtectedBytes < 0)
                {
                    throw new ArgumentException("CENC subsample lengths must be non-negative.", "subsamples");
                }

                pos += (long)ss.ClearBytes + ss.ProtectedBytes; // long avoids int overflow on the running sum
                if (pos > totalLength)
                {
                    throw new ArgumentException(
                        $"CENC subsample map ({pos} bytes) exceeds sample length ({totalLength}).", "subsamples");
                }
            }
        }

        /// <summary>
        /// A resumable AES-CTR keystream. <see cref="XorInto"/> consumes keystream bytes exactly equal to the
        /// number of bytes XORed, so interleaved clear gaps (skipped via separate calls) keep the keystream
        /// continuous across protected runs — the ISO 23001-7 'cenc' rule. The 16-byte counter increments
        /// big-endian across the full 128 bits; for an 8-byte IV the low 8 bytes are the block counter (the
        /// carry can never reach the IV octets within a single sample).
        /// </summary>
        private sealed class CtrKeystream
        {
            private readonly Aes _aes;
            private readonly byte[] _counter = new byte[16];
            private readonly byte[] _block = new byte[16];
            private int _blockPos = 16; // force a fresh block on first use

            public CtrKeystream(Aes aes, ReadOnlySpan<byte> counter)
            {
                _aes = aes;
                counter.CopyTo(_counter);
            }

            public void XorInto(Span<byte> data, int offset, int length)
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

    /// <summary>
    /// One-shot CENC sample decryptor. A thin wrapper over <see cref="CencDecryptor"/> for single calls and
    /// tests; for a whole track, construct one <see cref="CencDecryptor"/> and reuse it (the AES key schedule
    /// is then computed once instead of per sample).
    /// </summary>
    public static class CencSampleDecryptor
    {
        /// <summary>
        /// Decrypts <paramref name="sample"/> and returns a new array. See <see cref="CencDecryptor"/> for the
        /// parameter semantics. When <paramref name="subsamples"/> is <c>null</c> the whole sample is one run.
        /// </summary>
        public static byte[] DecryptSample(
            ReadOnlySpan<byte> sample,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> iv,
            CencProtectionScheme scheme,
            IReadOnlyList<CencSubsample>? subsamples = null,
            int cryptByteBlock = 0,
            int skipByteBlock = 0)
        {
            using var decryptor = new CencDecryptor(key, scheme);
            var output = sample.ToArray();
            ReadOnlySpan<CencSubsample> ss = subsamples is null ? default : ToArray(subsamples);
            decryptor.DecryptSampleInPlace(output, iv, ss, cryptByteBlock, skipByteBlock);
            return output;
        }

        private static CencSubsample[] ToArray(IReadOnlyList<CencSubsample> list)
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
