using System;
using System.Linq;
using System.Text;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Security.TokenProtection;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class StringTokenProtectorTests
    {
        private sealed class XorTokenProtector : ITokenProtector
        {
            public string AlgorithmId => "test-xor";

            public byte[] Protect(ReadOnlySpan<byte> plaintext)
            {
                var output = plaintext.ToArray();
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] ^= 0x5A;
                }

                return output;
            }

            public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
            {
                return Protect(protectedBytes);
            }
        }

        [Fact]
        public void Should_RoundTrip_UnicodeString()
        {
            var tokenProtector = new XorTokenProtector();
            var protector = new StringTokenProtector(tokenProtector);

            const string plaintext = "Miles Davis — Kind of Blue 🎷";
            var protectedValue = protector.Protect(plaintext);

            Assert.NotNull(protectedValue);
            Assert.True(protector.IsProtected(protectedValue));

            var roundTrip = protector.Unprotect(protectedValue);
            Assert.Equal(plaintext, roundTrip);
        }

        [Fact]
        public void Should_BeIdempotent_WhenProtectingAlreadyProtectedValue()
        {
            var tokenProtector = new XorTokenProtector();
            var protector = new StringTokenProtector(tokenProtector);

            const string plaintext = "secret";
            var once = protector.Protect(plaintext);
            var twice = protector.Protect(once);

            Assert.Equal(once, twice);
        }

        [Fact]
        public void Should_PassThrough_PlaintextValues()
        {
            var tokenProtector = new XorTokenProtector();
            var protector = new StringTokenProtector(tokenProtector);

            const string plaintext = "not-protected";
            Assert.Equal(plaintext, protector.Unprotect(plaintext));

            var ok = protector.TryUnprotect(plaintext, out var unprotected);
            Assert.False(ok);
            Assert.Equal(plaintext, unprotected);
        }

        [Fact]
        public void Should_Handle_NullAndEmpty()
        {
            var tokenProtector = new XorTokenProtector();
            var protector = new StringTokenProtector(tokenProtector);

            Assert.Null(protector.Protect(null));
            Assert.Null(protector.Unprotect(null));
            Assert.False(protector.TryUnprotect(null, out var nullResult));
            Assert.Null(nullResult);

            Assert.Equal(string.Empty, protector.Protect(string.Empty));
            Assert.Equal(string.Empty, protector.Unprotect(string.Empty));
            Assert.False(protector.TryUnprotect(string.Empty, out var emptyResult));
            Assert.Equal(string.Empty, emptyResult);
        }

        [Fact]
        public void Should_Reject_InvalidProtectedStringFormat()
        {
            var tokenProtector = new XorTokenProtector();
            var protector = new StringTokenProtector(tokenProtector);

            const string invalid = "lpc:ps:v1:YWxn:!!!not-base64url!!!";

            Assert.True(protector.IsProtected(invalid));
            Assert.False(protector.TryUnprotect(invalid, out _));
            Assert.Throws<InvalidOperationException>(() => protector.Unprotect(invalid));
        }

        [Fact]
        public void Should_Use_Base64UrlEncoding_WithoutPaddingOrReservedChars()
        {
            var tokenProtector = new XorTokenProtector();
            var protector = new StringTokenProtector(tokenProtector);

            var protectedValue = protector.Protect("value");
            Assert.NotNull(protectedValue);

            Assert.DoesNotContain("+", protectedValue, StringComparison.Ordinal);
            Assert.DoesNotContain("/", protectedValue, StringComparison.Ordinal);
            Assert.DoesNotContain("=", protectedValue, StringComparison.Ordinal);
        }

        [Fact]
        public void Should_Encode_AlgorithmId_InFirstSegment()
        {
            var tokenProtector = new XorTokenProtector();
            var protector = new StringTokenProtector(tokenProtector);

            var protectedValue = protector.Protect("value");
            Assert.NotNull(protectedValue);

            var remainder = protectedValue!.Substring("lpc:ps:v1:".Length);
            var parts = remainder.Split(':');
            Assert.Equal(2, parts.Length);

            var algBytes = Base64UrlDecode(parts[0]);
            var alg = Encoding.UTF8.GetString(algBytes);
            Assert.Equal(tokenProtector.AlgorithmId, alg);
        }

        private static byte[] Base64UrlDecode(string base64Url)
        {
            var padded = base64Url.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2:
                    padded += "==";
                    break;
                case 3:
                    padded += "=";
                    break;
            }

            return Convert.FromBase64String(padded);
        }
    }
}

