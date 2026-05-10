using System;
using System.Security.Cryptography;
using System.Text;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Utilities;

public class HashingUtilityTests
{
    // MD5

    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("The quick brown fox jumps over the lazy dog", "9e107d9d372bb6826bd81d3542a419d6")]
    public void ComputeMD5Hash_ProducesKnownVectors(string input, string expected)
    {
        Assert.Equal(expected, HashingUtility.ComputeMD5Hash(input));
    }

    [Fact]
    public void ComputeMD5Hash_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HashingUtility.ComputeMD5Hash(null!));
    }

    [Fact]
    public void ComputeMD5Hash_OutputIsLowercaseHex32Chars()
    {
        var hash = HashingUtility.ComputeMD5Hash("anything");
        Assert.Equal(32, hash.Length);
        Assert.Matches("^[0-9a-f]+$", hash);
    }

    // SHA-256

    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void ComputeSHA256_ProducesKnownVectors(string input, string expected)
    {
        Assert.Equal(expected, HashingUtility.ComputeSHA256(input));
    }

    [Fact]
    public void ComputeSHA256_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HashingUtility.ComputeSHA256(null!));
    }

    [Fact]
    public void ComputeSHA256_OutputIsLowercaseHex64Chars()
    {
        var hash = HashingUtility.ComputeSHA256("anything");
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]+$", hash);
    }

    // HMAC-SHA256

    [Fact]
    public void ComputeHmacSha256_KnownVector_RFC4231TestCase1()
    {
        // RFC 4231 test case 1: key = 0x0b repeated 20 times, data = "Hi There".
        // HashingUtility UTF-8-encodes string secrets — \v (0x0B) is one byte in UTF-8,
        // so this reproduces the RFC vector exactly.
        var key = new string('\v', 20);
        var data = "Hi There";
        var expected = "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7";
        Assert.Equal(expected, HashingUtility.ComputeHmacSha256(key, data));
    }

    [Fact]
    public void ComputeHmacSha256_DifferentSecrets_ProduceDifferentMacs()
    {
        var a = HashingUtility.ComputeHmacSha256("secret-a", "payload");
        var b = HashingUtility.ComputeHmacSha256("secret-b", "payload");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeHmacSha256_NullSecret_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HashingUtility.ComputeHmacSha256(null!, "data"));
    }

    [Fact]
    public void ComputeHmacSha256_NullData_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HashingUtility.ComputeHmacSha256("secret", null!));
    }

    [Fact]
    public void ComputeHmacSha256_AgreesWithStdlibImplementation()
    {
        var key = "shared-secret";
        var data = "request|nonce|2026-05-09T00:00:00Z";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

        Assert.Equal(expected, HashingUtility.ComputeHmacSha256(key, data));
    }

    // GenerateCacheKey

    [Fact]
    public void GenerateCacheKey_StableForSameComponents()
    {
        var k1 = HashingUtility.GenerateCacheKey("user", "42", "playlist");
        var k2 = HashingUtility.GenerateCacheKey("user", "42", "playlist");
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void GenerateCacheKey_OrderSensitive()
    {
        var k1 = HashingUtility.GenerateCacheKey("a", "b");
        var k2 = HashingUtility.GenerateCacheKey("b", "a");
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void GenerateCacheKey_BoundaryComponentsDisambiguated()
    {
        // The pipe delimiter prevents collision between ("ab","c") and ("a","bc").
        var k1 = HashingUtility.GenerateCacheKey("ab", "c");
        var k2 = HashingUtility.GenerateCacheKey("a", "bc");
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void GenerateCacheKey_NullComponents_Throws()
    {
        Assert.Throws<ArgumentException>(() => HashingUtility.GenerateCacheKey(null!));
    }

    [Fact]
    public void GenerateCacheKey_EmptyComponents_Throws()
    {
        Assert.Throws<ArgumentException>(() => HashingUtility.GenerateCacheKey(Array.Empty<string>()));
    }

    [Fact]
    public void GenerateCacheKey_OutputIsMD5Hex()
    {
        var key = HashingUtility.GenerateCacheKey("x");
        Assert.Equal(32, key.Length);
        Assert.Matches("^[0-9a-f]+$", key);
    }
}
