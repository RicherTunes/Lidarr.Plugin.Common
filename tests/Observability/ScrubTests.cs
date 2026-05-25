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

    // ── Parity with LogRedactor.IsSensitiveParameter (Wave 17F unification) ─────
    // Before this wave Scrub.Url's regex hand-listed sensitive names but LogRedactor's
    // IsSensitiveParameter knew about more exact names (signature, sessionid, credential)
    // AND used a contains-rule for compound names (my_secret_token, app_password).
    // Unifying so both surfaces agree — same param recognised whether logged via
    // structured property or substituted into a URL string.

    [Theory]
    [InlineData("https://api.example.com/sign?signature=abc123def")]
    [InlineData("https://api.example.com/sign?Signature=abc123def")]
    [InlineData("https://api.example.com/req?request_sig=xyz789")]
    public void Url_SignatureParam_Redacted(string url)
    {
        var result = Scrub.Url(url);
        Assert.DoesNotContain("abc123def", result);
        Assert.DoesNotContain("xyz789", result);
    }

    [Theory]
    [InlineData("https://api.example.com/play?sessionid=sess-abc")]
    [InlineData("https://api.example.com/play?session_id=sess-abc")]
    [InlineData("https://api.example.com/play?session=sess-abc")]
    public void Url_SessionParam_Redacted(string url)
    {
        var result = Scrub.Url(url);
        Assert.DoesNotContain("sess-abc", result);
    }

    [Theory]
    [InlineData("https://api.example.com/login?credential=u%3Ap")]
    [InlineData("https://api.example.com/login?credentials=u%3Ap")]
    public void Url_CredentialParam_Redacted(string url)
    {
        var result = Scrub.Url(url);
        Assert.DoesNotContain("u%3Ap", result);
    }

    [Theory]
    [InlineData("https://api.example.com/call?my_secret_token=zzz")]
    [InlineData("https://api.example.com/call?app_password=zzz")]
    [InlineData("https://api.example.com/call?user_auth_blob=zzz")]
    [InlineData("https://api.example.com/call?private_key=zzz")]
    public void Url_CompoundSensitiveParamName_Redacted_ViaContainsRule(string url)
    {
        // LogRedactor.IsSensitiveParameter uses a contains-rule for these terms:
        // secret, password, token, auth, credential, key, apikey. Scrub.Url should
        // honor the same rule so a parameter named "my_secret_token" is caught.
        var result = Scrub.Url(url);
        Assert.DoesNotContain("zzz", result);
    }

    [Fact]
    public void Url_CompoundNonSensitive_NotRedacted()
    {
        // A param whose name contains a sensitive term as a substring legitimately
        // (e.g. "keyboard" contains "key", "secretariat" contains "secret") would
        // false-positive-redact. The contains-rule treats this as acceptable — secrets
        // leaking into logs is worse than a UI param value being masked. This test
        // pins that behaviour so future readers know it's intentional, not a bug.
        const string url = "https://example.com/?keyboard=qwerty";
        var result = Scrub.Url(url);
        // "key" is a sensitive contains-term, so this DOES get redacted.
        Assert.DoesNotContain("qwerty", result);
    }

    [Fact]
    public void Url_NonSensitiveParamsRemainUnchangedAfterUnification()
    {
        // Regression: the refactor must not start redacting q/limit/offset/filter etc.
        const string url = "https://api.example.com/tracks?q=radiohead&limit=10&offset=0&filter=studio";
        Assert.Equal(url, Scrub.Url(url));
    }

    // ================================================================== //
    // Scrub.UrlAndStripQuery
    // ================================================================== //
    //
    // The conservative sibling to Scrub.Url: drops the entire query string +
    // fragment instead of selectively redacting known-sensitive parameter values.
    // Use when you can't enumerate the sensitive parameters ahead of time —
    // signed CDN URLs, HLS streams with rotating tokens, vendor proprietary
    // query schemas. The cost is losing any non-sensitive context (region,
    // format, expires) from the logged URL; the upside is no leak risk if a
    // new sensitive param name appears upstream.

    [Fact]
    public void UrlAndStripQuery_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Scrub.UrlAndStripQuery(null!));
    }

    [Fact]
    public void UrlAndStripQuery_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Scrub.UrlAndStripQuery(string.Empty));
    }

    [Fact]
    public void UrlAndStripQuery_NoQueryString_ReturnedUnchanged()
    {
        const string url = "https://cdn.example.com/v1/segment.ts";
        Assert.Equal(url, Scrub.UrlAndStripQuery(url));
    }

    [Fact]
    public void UrlAndStripQuery_DropsAllQueryParams_SensitiveAndNot()
    {
        // Unlike Scrub.Url which preserves q=music, this strips EVERYTHING after '?'.
        const string url = "https://cdn.example.com/seg.ts?token=abc&sig=def&format=ts&expires=1234";
        Assert.Equal("https://cdn.example.com/seg.ts", Scrub.UrlAndStripQuery(url));
    }

    [Fact]
    public void UrlAndStripQuery_DropsFragment()
    {
        const string url = "https://cdn.example.com/playlist.m3u8#variant-3";
        Assert.Equal("https://cdn.example.com/playlist.m3u8", Scrub.UrlAndStripQuery(url));
    }

    [Fact]
    public void UrlAndStripQuery_DropsFragmentAndQuery()
    {
        const string url = "https://cdn.example.com/playlist.m3u8?token=abc#variant-3";
        Assert.Equal("https://cdn.example.com/playlist.m3u8", Scrub.UrlAndStripQuery(url));
    }

    [Fact]
    public void UrlAndStripQuery_PreservesPath_IncludingSegmentsThatLookLikeQueries()
    {
        // The slash-segments before '?' MUST be preserved even if they contain
        // characters that look like query syntax in other contexts.
        const string url = "https://cdn.example.com/v1/streams/abc123/segment.ts?token=x";
        Assert.Equal("https://cdn.example.com/v1/streams/abc123/segment.ts", Scrub.UrlAndStripQuery(url));
    }

    [Fact]
    public void UrlAndStripQuery_PreservesPort()
    {
        const string url = "https://localhost:8443/api/test?key=secret";
        Assert.Equal("https://localhost:8443/api/test", Scrub.UrlAndStripQuery(url));
    }

    [Fact]
    public void UrlAndStripQuery_HttpScheme_Works()
    {
        const string url = "http://example.com/path?token=x";
        Assert.Equal("http://example.com/path", Scrub.UrlAndStripQuery(url));
    }

    [Fact]
    public void UrlAndStripQuery_RelativeOrInvalidUrl_ReturnsInputUnchanged()
    {
        // No scheme/host to parse — return as-is. Caller can detect this by
        // checking whether the result equals the input.
        Assert.Equal("not a url", Scrub.UrlAndStripQuery("not a url"));
        Assert.Equal("/just/a/path", Scrub.UrlAndStripQuery("/just/a/path"));
    }
}
