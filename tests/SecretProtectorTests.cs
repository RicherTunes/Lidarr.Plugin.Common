using System;
using System.Security.Cryptography;
using System.Text;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Security.SecretProtection;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public sealed class SecretProtectorTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Protect_ShouldReturnEmpty_ForNullOrWhitespace(string? value)
        {
            var protector = Create();
            Assert.Equal(string.Empty, protector.Protect(value));
        }

        [Fact]
        public void Protect_ShouldReturnV2Envelope()
        {
            var protector = Create();
            var protectedValue = protector.Protect("hello");

            Assert.StartsWith("enc:v2:", protectedValue, StringComparison.Ordinal);
            Assert.Contains(":test:", protectedValue, StringComparison.Ordinal);
        }

        [Fact]
        public void Unprotect_ShouldRoundTrip_V2()
        {
            var protector = Create();
            var protectedValue = protector.Protect("hello");

            Assert.Equal("hello", protector.Unprotect(protectedValue));
        }

        [Fact]
        public void Unprotect_ShouldReturnPlaintext_WhenNotPrefixed()
        {
            var protector = Create();
            Assert.Equal("plain", protector.Unprotect("plain"));
        }

        [Fact]
        public void Unprotect_ShouldReturnEmpty_ForUnknownEncPrefix()
        {
            var protector = Create();
            Assert.Equal(string.Empty, protector.Unprotect("enc:v3:whatever"));
        }

        [Fact]
        public void Unprotect_ShouldReturnEmpty_ForInvalidV2Payload()
        {
            var protector = Create();
            Assert.Equal(string.Empty, protector.Unprotect("enc:v2:test:***not-base64***"));
        }

        [Fact]
        public void Unprotect_ShouldReturnEmpty_WhenV1AndNoLegacyKey()
        {
            var protector = Create();
            Assert.Equal(string.Empty, protector.Unprotect("enc:v1:AA==.AA==.AA=="));
        }

        [Fact]
        public void Unprotect_ShouldReadLegacyV1_WhenLegacyKeyProvided()
        {
            var legacyKey = new byte[32];
            for (var i = 0; i < legacyKey.Length; i++) legacyKey[i] = (byte)i;

            var value = "legacy-value";
            var v1 = CreateLegacyV1(value, legacyKey);

            var protector = Create(legacyKey);
            Assert.Equal(value, protector.Unprotect(v1));
        }

        private static ISecretProtector Create(byte[]? legacyKey = null)
        {
            return new SecretProtector(new FakeTokenProtector(), legacyKey);
        }

        private static string CreateLegacyV1(string value, byte[] key)
        {
            var plaintext = Encoding.UTF8.GetBytes(value);
            var nonce = new byte[12];
            for (var i = 0; i < nonce.Length; i++) nonce[i] = (byte)(255 - i);

            var cipher = new byte[plaintext.Length];
            var tag = new byte[16];

            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, cipher, tag);

            return "enc:v1:" + string.Join('.',
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(cipher),
                Convert.ToBase64String(tag));
        }

        private sealed class FakeTokenProtector : ITokenProtector
        {
            public string AlgorithmId => "test";

            public byte[] Protect(ReadOnlySpan<byte> plaintext)
            {
                var bytes = plaintext.ToArray();
                Array.Reverse(bytes);
                return bytes;
            }

            public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
            {
                var bytes = protectedBytes.ToArray();
                Array.Reverse(bytes);
                return bytes;
            }
        }
    }
}

