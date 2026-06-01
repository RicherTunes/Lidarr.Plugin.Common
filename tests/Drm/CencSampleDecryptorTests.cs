using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.Services.Drm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Verifies the shared CENC sample decryptor against canonical published test vectors so the AES math
    /// (CTR keystream for 'cenc', CBC for 'cbcs', and subsample partitioning) is provably correct without
    /// any live DRM credentials.
    /// </summary>
    public sealed class CencSampleDecryptorTests
    {
        // NIST SP 800-38A, F.5 CTR-AES128. AES-CTR is symmetric, so decrypting the published ciphertext
        // with the published key + initial counter (used as the 16-byte CENC IV) must yield the plaintext.
        [Fact]
        public void DecryptSample_Cenc_WholeSample_MatchesNistCtrVector()
        {
            var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
            var iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
            var ciphertext = Convert.FromHexString(
                "874d6191b620e3261bef6864990db6ce" +
                "9806f66b7970fdff8617187bb9fffdff" +
                "5ae4df3edbd5d35e5b4f09020db03eab" +
                "1e031dda2fbe03d1792170a0f3009cee");
            var expectedPlaintext = Convert.FromHexString(
                "6bc1bee22e409f96e93d7e117393172a" +
                "ae2d8a571e03ac9c9eb76fac45af8e51" +
                "30c81c46a35ce411e5fbc1191a0a52ef" +
                "f69f2445df4f9b17ad2b417be66c3710");

            var plaintext = CencSampleDecryptor.DecryptSample(
                ciphertext, key, iv, CencProtectionScheme.Cenc, subsamples: null);

            Assert.Equal(expectedPlaintext, plaintext);
        }

        // NIST SP 800-38A, F.2 CBC-AES128. 'cbcs' whole-sample decryption is plain AES-CBC with no padding.
        [Fact]
        public void DecryptSample_Cbcs_WholeSample_MatchesNistCbcVector()
        {
            var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
            var iv = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
            var ciphertext = Convert.FromHexString(
                "7649abac8119b246cee98e9b12e9197d" +
                "5086cb9b507219ee95db113a917678b2" +
                "73bed6b8e3c1743b7116e69e22229516" +
                "3ff1caa1681fac09120eca307586e1a7");
            var expectedPlaintext = Convert.FromHexString(
                "6bc1bee22e409f96e93d7e117393172a" +
                "ae2d8a571e03ac9c9eb76fac45af8e51" +
                "30c81c46a35ce411e5fbc1191a0a52ef" +
                "f69f2445df4f9b17ad2b417be66c3710");

            var plaintext = CencSampleDecryptor.DecryptSample(
                ciphertext, key, iv, CencProtectionScheme.Cbcs, subsamples: null);

            Assert.Equal(expectedPlaintext, plaintext);
        }

        // CENC 'cenc' subsample rule (ISO 23001-7): the AES-CTR keystream is applied ONLY to the protected
        // bytes, continuously across subsample boundaries; clear bytes pass through and do NOT consume
        // keystream. Built from the NIST CTR vector: two protected runs (10 + 30 bytes) interleaved with
        // clear runs must decrypt as the first 40 keystream bytes applied to the concatenated protected data.
        [Fact]
        public void DecryptSample_Cenc_Subsamples_DecryptsProtectedOnly_WithContinuousKeystream()
        {
            var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
            var iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
            var ct = Convert.FromHexString(
                "874d6191b620e3261bef6864990db6ce" +
                "9806f66b7970fdff8617187bb9fffdff" +
                "5ae4df3edbd5d35e5b4f09020db03eab");
            var pt = Convert.FromHexString(
                "6bc1bee22e409f96e93d7e117393172a" +
                "ae2d8a571e03ac9c9eb76fac45af8e51" +
                "30c81c46a35ce411e5fbc1191a0a52ef");

            var clearA = new byte[] { 0x11, 0x22, 0x33 };
            var clearB = new byte[] { 0x44, 0x55 };

            var sample = Concat(clearA, ct[0..10], clearB, ct[10..40]);
            var expected = Concat(clearA, pt[0..10], clearB, pt[10..40]);

            var subsamples = new List<CencSubsample>
            {
                new(ClearBytes: 3, ProtectedBytes: 10),
                new(ClearBytes: 2, ProtectedBytes: 30),
            };

            var result = CencSampleDecryptor.DecryptSample(
                sample, key, iv, CencProtectionScheme.Cenc, subsamples);

            Assert.Equal(expected, result);
        }

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    }
}
