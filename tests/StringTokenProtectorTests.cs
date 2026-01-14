using System;
using System.Linq;
using System.Text;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Security.TokenProtection;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public class StringTokenProtectorTests
{
    [Fact]
    public void Protect_Null_ReturnsNull()
    {
        var sut = Create("alg");
        Assert.Null(sut.Protect(null));
    }

    [Fact]
    public void Protect_Empty_ReturnsEmpty()
    {
        var sut = Create("alg");
        Assert.Equal(string.Empty, sut.Protect(string.Empty));
    }

    [Fact]
    public void Unprotect_Null_ReturnsNull()
    {
        var sut = Create("alg");
        Assert.Null(sut.Unprotect(null));
    }

    [Fact]
    public void Unprotect_Empty_ReturnsEmpty()
    {
        var sut = Create("alg");
        Assert.Equal(string.Empty, sut.Unprotect(string.Empty));
    }

    [Fact]
    public void Protect_And_Unprotect_Roundtrip_ReturnsOriginal()
    {
        var sut = Create("alg");
        const string value = "hello world";

        var protectedValue = sut.Protect(value);

        Assert.NotNull(protectedValue);
        Assert.StartsWith("enc:v1:alg:", protectedValue, StringComparison.Ordinal);
        Assert.Equal(value, sut.Unprotect(protectedValue));
    }

    [Fact]
    public void Unprotect_NonProtectedValue_ReturnsInputUnchanged()
    {
        var sut = Create("alg");
        const string value = "plaintext";

        Assert.Equal(value, sut.Unprotect(value));
    }

    [Fact]
    public void Protect_ValidProtectedString_IsIdempotent()
    {
        var sut = Create("alg");
        const string value = "secret";

        var once = sut.Protect(value);
        var twice = sut.Protect(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Protect_WrongVersion_Throws()
    {
        var sut = Create("alg");
        var protectedValue = "enc:v2:alg:AAAA";

        Assert.Throws<NotSupportedException>(() => sut.Protect(protectedValue));
    }

    [Fact]
    public void Protect_AlgorithmMismatch_Throws()
    {
        var sut = Create("alg");
        var protectedValue = "enc:v1:other:AAAA";

        Assert.Throws<InvalidOperationException>(() => sut.Protect(protectedValue));
    }

    [Fact]
    public void Protect_InvalidPayload_ThrowsWithoutLeakingPayload()
    {
        var sut = Create("alg");
        var payload = "NOT_BASE64_$$$";
        var protectedValue = $"enc:v1:alg:{payload}";

        var ex = Assert.Throws<FormatException>(() => sut.Protect(protectedValue));
        Assert.DoesNotContain(payload, ex.ToString());
    }

    [Fact]
    public void Protect_DecryptFailure_ThrowsWithoutLeakingPayload()
    {
        var sut = Create("alg");
        var payloadBytes = new byte[] { 0xFF, 0x00, 0x01 };
        var payload = Convert.ToBase64String(payloadBytes);
        var protectedValue = $"enc:v1:alg:{payload}";

        var ex = Assert.Throws<InvalidOperationException>(() => sut.Protect(protectedValue));
        Assert.DoesNotContain(payload, ex.ToString());
    }

    [Fact]
    public void Unprotect_InvalidPayload_ThrowsWithoutLeakingPayload()
    {
        var sut = Create("alg");
        var payload = "NOT_BASE64_$$$";
        var protectedValue = $"enc:v1:alg:{payload}";

        var ex = Assert.Throws<FormatException>(() => sut.Unprotect(protectedValue));
        Assert.DoesNotContain(payload, ex.ToString());
    }

    [Fact]
    public void Unprotect_AlgorithmMismatch_Throws()
    {
        var sut = Create("alg");
        var protectedValue = "enc:v1:other:AAAA";

        Assert.Throws<InvalidOperationException>(() => sut.Unprotect(protectedValue));
    }

    [Fact]
    public void Protect_Unicode_Roundtrips()
    {
        var sut = Create("alg");
        const string value = "日本語 🔒 café";

        var protectedValue = sut.Protect(value);
        Assert.Equal(value, sut.Unprotect(protectedValue));
    }

    private static StringTokenProtector Create(string algorithmId)
    {
        return new StringTokenProtector(new FakeTokenProtector(algorithmId));
    }

    private sealed class FakeTokenProtector : ITokenProtector
    {
        private static readonly byte[] Sentinel = { 0x50, 0x52, 0x4F, 0x54 }; // "PROT"

        public FakeTokenProtector(string algorithmId)
        {
            AlgorithmId = algorithmId;
        }

        public string AlgorithmId { get; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var bytes = plaintext.ToArray();
            Array.Reverse(bytes);

            return Sentinel.Concat(bytes).ToArray();
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            var bytes = protectedBytes.ToArray();
            if (bytes.Length < Sentinel.Length || !bytes.Take(Sentinel.Length).SequenceEqual(Sentinel))
            {
                throw new InvalidOperationException("Ciphertext not recognized.");
            }

            var payload = bytes.Skip(Sentinel.Length).ToArray();
            Array.Reverse(payload);
            return payload;
        }
    }
}
