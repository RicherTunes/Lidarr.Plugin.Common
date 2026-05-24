using Lidarr.Plugin.Common.Observability;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Observability;

/// <summary>
/// Targeted regression tests for sensitive shapes the generic 32+-char
/// pattern misses: short opaque vendor secrets, OAuth query-string tokens,
/// and OS-level user-paths that leak install layout.
/// </summary>
public sealed class LogRedactorShortTokenAndPathTests
{
    // Qobuz app_secret: 12-24 hex chars, derived from the bundle.
    // Without a dedicated pattern, the generic 32+ catch-all misses it
    // entirely and leaks the live signing key.
    [Theory]
    [InlineData("a1b2c3d4e5f6")]                    // 12 hex
    [InlineData("0123456789abcdef01234567")]        // 24 hex
    [InlineData("deadbeefdeadbeefdead")]            // 20 hex
    public void Redact_ShortHexSecret_InContext_IsRedacted(string secret)
    {
        var line = $"derived app_secret={secret}";
        var actual = LogRedactor.Redact(line);
        Assert.DoesNotContain(secret, actual);
    }

    [Fact]
    public void Redact_KeyValueShape_SecretEquals_IsRedacted()
    {
        // Less context, same shape: `secret=<hex>` in a URL or log line.
        var line = "POST /auth?app_id=1&secret=a1b2c3d4e5f6 → 200";
        var actual = LogRedactor.Redact(line);
        Assert.DoesNotContain("a1b2c3d4e5f6", actual);
    }

    // OAuth callback URL query params. `code` and `state` are 8-20 chars,
    // well below the 32-char generic threshold; no Authorization/Bearer
    // prefix; no JSON wrapping; so they survived round-1 unredacted.
    [Theory]
    [InlineData("code")]
    [InlineData("state")]
    [InlineData("access_token")]
    [InlineData("id_token")]
    [InlineData("client_secret")]
    [InlineData("code_verifier")]
    [InlineData("refresh_token")]
    public void Redact_OAuthShortToken_QueryParam_IsRedacted(string paramName)
    {
        var value = "abc123XYZ"; // 9 chars
        var line = $"https://service.example/auth?{paramName}={value}&other=safe";
        var actual = LogRedactor.Redact(line);
        Assert.DoesNotContain(value, actual);
    }

    [Fact]
    public void Redact_OAuthCallbackUrl_BothCodeAndState_Redacted()
    {
        var line = "Parsed: https://tidal.com/cb?code=auth_xyz_short&state=opaque123";
        var actual = LogRedactor.Redact(line);
        Assert.DoesNotContain("auth_xyz_short", actual);
        Assert.DoesNotContain("opaque123", actual);
    }

    // OS-level user-path leak: home-directory username is PII + reveals
    // install layout to anyone reading shared logs.
    [Theory]
    [InlineData(@"C:\Users\Alexandre\.claude\.credentials.json", "Alexandre")]
    [InlineData(@"C:\Users\jdoe\AppData\Local\Brainarr", "jdoe")]
    [InlineData(@"/home/alex/.config/qobuz/session.json", "alex")]
    [InlineData(@"/home/ubuntu/lidarr/plugins/tidal", "ubuntu")]
    public void Redact_UserHomePathContainsUsername_IsRedacted(string path, string username)
    {
        var actual = LogRedactor.Redact(path);
        Assert.DoesNotContain(username, actual);
    }

    [Fact]
    public void Redact_PreservesNonSensitivePathComponents()
    {
        // After redacting the user segment, the rest of the path should
        // remain useful for debugging (e.g. you can still tell whether
        // the file is under .config/qobuz vs .config/brainarr).
        var actual = LogRedactor.Redact("/home/alice/.config/qobuz/session.json");
        Assert.Contains(".config", actual);
        Assert.Contains("qobuz", actual);
        Assert.Contains("session.json", actual);
    }

    // Negative-space tests: bare-word `code=`/`state=`/`signature=`/`sig=`
    // and plain `secret=` appear in normal logs as non-credential labels
    // (HTTP status code, application state, method signature, etc.).
    // These MUST NOT be redacted; only the URL-query-context occurrences
    // are real credentials.

    [Theory]
    [InlineData("Request completed with status code=200")]
    [InlineData("Error: error code=NETWORK_TIMEOUT")]
    [InlineData("Validator returned code=invalid_format")]
    [InlineData("Application state=Running")]
    [InlineData("state=available pool=20")]
    [InlineData("Mismatched method signature=void Foo(int)")]
    [InlineData("Hash sig=sha256 (computed)")]
    [InlineData("It's not a secret=true that we ship beta features")]
    public void Redact_BareAmbiguousKey_NotInQueryContext_IsLeftAlone(string line)
    {
        // The narrowed regex requires a query-string delimiter (? & ;) BEFORE
        // the key for ambiguous names. Plain `state=` / `code=` / etc. in
        // ordinary log content must pass through unchanged.
        var actual = LogRedactor.Redact(line);
        Assert.Equal(line, actual);
    }

    [Theory]
    [InlineData("https://srv/cb?code=xyz123", "xyz123")]
    [InlineData("https://srv/cb?other=1&code=xyz123", "xyz123")]
    [InlineData("https://srv/cb?other=1;state=opaque", "opaque")]
    [InlineData("POST /auth?app_id=1&secret=a1b2c3", "a1b2c3")]
    [InlineData("/api/sign?signature=abc&other=ok", "abc")]
    [InlineData("/api/v1?sig=deadbeef&kind=md5", "deadbeef")]
    public void Redact_AmbiguousKey_InQueryContext_IsRedacted(string line, string secretValue)
    {
        var actual = LogRedactor.Redact(line);
        Assert.DoesNotContain(secretValue, actual);
    }
}
