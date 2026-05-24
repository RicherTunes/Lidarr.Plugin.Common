using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.Observability;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Observability;

/// <summary>
/// Tests for <see cref="Scrub"/> — null/empty, short strings, URL sensitive params,
/// header redaction, mixed-case parameter names.
/// </summary>
public class ScrubTests
{
    // ================================================================== //
    // Scrub.Secret
    // ================================================================== //

    [Fact]
    public void Secret_Null_ReturnsThreeStars()
    {
        Assert.Equal("***", Scrub.Secret(null));
    }

    [Fact]
    public void Secret_Empty_ReturnsThreeStars()
    {
        Assert.Equal("***", Scrub.Secret(string.Empty));
    }

    [Fact]
    public void Secret_ShorterThanLeadingVisible_ReturnsThreeStars()
    {
        // "ab" length (2) <= leadingVisible=3
        Assert.Equal("***", Scrub.Secret("ab"));
    }

    [Fact]
    public void Secret_ExactlyLeadingVisible_ReturnsThreeStars()
    {
        // value.Length == leadingVisible → still fully masked
        Assert.Equal("***", Scrub.Secret("abc", leadingVisible: 3));
    }

    [Fact]
    public void Secret_LongerThanLeadingVisible_PreservesPrefix()
    {
        Assert.Equal("sk-***", Scrub.Secret("sk-abcdefgh", leadingVisible: 3));
    }

    [Fact]
    public void Secret_DefaultLeadingVisible_Is3()
    {
        Assert.Equal("ABC***", Scrub.Secret("ABCDEF"));
    }

    [Fact]
    public void Secret_LeadingVisibleZero_AlwaysReturnsMask()
    {
        Assert.Equal("***", Scrub.Secret("anything", leadingVisible: 0));
    }

    [Fact]
    public void Secret_NegativeLeadingVisible_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Scrub.Secret("value", leadingVisible: -1));
    }

    [Fact]
    public void Secret_TokenShaped_MasksCorrectly()
    {
        var result = Scrub.Secret("sk-abc123xyz");
        Assert.Equal("sk-***", result);
        Assert.DoesNotContain("abc123xyz", result);
    }

    // ================================================================== //
    // Scrub.Headers
    // ================================================================== //

    [Fact]
    public void Headers_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Scrub.Headers(null!));
    }

    [Fact]
    public void Headers_Empty_ReturnsEmpty()
    {
        var result = Scrub.Headers(new Dictionary<string, string>());
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("AUTHORIZATION")]
    [InlineData("X-API-Key")]
    [InlineData("x-api-key")]
    [InlineData("X-Auth-Token")]
    [InlineData("x-auth-token")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("Proxy-Authorization")]
    public void Headers_SensitiveHeader_ValueIsRedacted(string headerName)
    {
        var headers = new Dictionary<string, string> { [headerName] = "Bearer super-secret-token" };
        var result = Scrub.Headers(headers);
        Assert.Equal("***", result[headerName]);
    }

    [Fact]
    public void Headers_NonSensitiveHeader_ValuePreserved()
    {
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "text/html",
        };
        var result = Scrub.Headers(headers);
        Assert.Equal("application/json", result["Content-Type"]);
        Assert.Equal("text/html", result["Accept"]);
    }

    [Fact]
    public void Headers_MixedSensitiveAndNonSensitive_OnlySensitiveRedacted()
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer token123",
            ["Content-Type"] = "application/json",
            ["X-API-Key"] = "my-key",
            ["User-Agent"] = "TestAgent/1.0",
        };
        var result = Scrub.Headers(headers);
        Assert.Equal("***", result["Authorization"]);
        Assert.Equal("***", result["X-API-Key"]);
        Assert.Equal("application/json", result["Content-Type"]);
        Assert.Equal("TestAgent/1.0", result["User-Agent"]);
    }

    [Fact]
    public void Headers_DoesNotMutateInput()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret" };
        _ = Scrub.Headers(headers);
        Assert.Equal("Bearer secret", headers["Authorization"]);
    }

    // ================================================================== //
    // Scrub.Url
    // ================================================================== //

    [Fact]
    public void Url_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Scrub.Url(null!));
    }

    [Fact]
    public void Url_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Scrub.Url(string.Empty));
    }

    [Fact]
    public void Url_NoQueryString_ReturnedUnchanged()
    {
        const string url = "https://api.example.com/v1/tracks";
        Assert.Equal(url, Scrub.Url(url));
    }

    [Theory]
    [InlineData("https://api.example.com/v1/tracks?api_key=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?apikey=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?api-key=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?token=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?access_token=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?refresh_token=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?key=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?secret=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?password=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?pwd=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?authorization=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?bearer=super-secret")]
    [InlineData("https://api.example.com/v1/tracks?auth=super-secret")]
    public void Url_KnownSensitiveParam_ValueRedacted(string url)
    {
        var result = Scrub.Url(url);
        Assert.DoesNotContain("super-secret", result);
        Assert.Contains("***", result);
    }

    [Fact]
    public void Url_MultipleSensitiveParams_AllRedacted()
    {
        const string url = "https://api.example.com/search?token=tok123&api_key=key456&q=music";
        var result = Scrub.Url(url);
        Assert.DoesNotContain("tok123", result);
        Assert.DoesNotContain("key456", result);
        Assert.Contains("q=music", result);  // non-sensitive param is preserved
    }

    [Fact]
    public void Url_MixedCaseParamName_StillRedacted()
    {
        const string url = "https://example.com/?API_KEY=secret&Token=tok";
        var result = Scrub.Url(url);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("tok", result);
    }

    [Fact]
    public void Url_NonSensitiveParams_Preserved()
    {
        const string url = "https://api.example.com/tracks?q=radiohead&limit=10&offset=0";
        Assert.Equal(url, Scrub.Url(url));
    }

    [Fact]
    public void Url_PathAndHostPreserved_WhenQueryRedacted()
    {
        const string url = "https://api.qobuz.com/v1/album/get?app_id=123&token=secret";
        var result = Scrub.Url(url);
        Assert.StartsWith("https://api.qobuz.com/v1/album/get", result);
        Assert.DoesNotContain("secret", result);
    }
}
