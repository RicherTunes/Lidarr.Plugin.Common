using Lidarr.Plugin.Common.Observability;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Observability;

/// <summary>
/// #25 (LOOP-013): log redaction as a testable CONTRACT for the ecosystem's vendor-specific
/// credential keys. Each plugin authenticates to a different backend and logs raw HTTP
/// bodies/headers/URLs during diagnostics, so the redactor must strip vendor token keys
/// regardless of casing (camelCase, Pascal-Case header) or form (JSON value, key=value, header).
///
/// The sentinels are deliberately SHORT and unstructured (no <c>sk-</c> prefix, &lt; 32 chars,
/// not email/IP/CC-shaped) so the generic 32-char catch-all CANNOT mask them — only a
/// name-anchored pattern can. That makes each assertion a real regression gate: if the
/// name-anchored redaction for that vendor key regresses, the sentinel survives and the test fails.
/// </summary>
public class LogRedactorVendorKeyContractTests
{
    // --- Apple Music: developerToken (JWT) + Music-User-Token header ---

    [Fact]
    public void Redact_AppleDeveloperToken_JsonForm_Stripped()
    {
        var result = LogRedactor.Redact("{\"developerToken\":\"appleDevTok7\"}");
        Assert.DoesNotContain("appleDevTok7", result);
        Assert.Contains(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void Redact_AppleDeveloperToken_KeyValueForm_Stripped()
    {
        var result = LogRedactor.Redact("developerToken=appleDevKv9");
        Assert.DoesNotContain("appleDevKv9", result);
    }

    [Fact]
    public void Redact_AppleMusicUserToken_JsonForm_Stripped()
    {
        var result = LogRedactor.Redact("{\"MusicUserToken\":\"appleMut3\"}");
        Assert.DoesNotContain("appleMut3", result);
    }

    [Fact]
    public void Redact_AppleMusicUserToken_HeaderForm_Stripped()
    {
        var result = LogRedactor.Redact("Music-User-Token: appleMutHdr5");
        Assert.DoesNotContain("appleMutHdr5", result);
    }

    // --- Amazon Music: appSecret (JSON form) + devicePrivateKey ---

    [Fact]
    public void Redact_AmazonAppSecret_JsonForm_Stripped()
    {
        var result = LogRedactor.Redact("{\"appSecret\":\"amzAppSec2\"}");
        Assert.DoesNotContain("amzAppSec2", result);
    }

    [Fact]
    public void Redact_AmazonDevicePrivateKey_KeyValueForm_Stripped()
    {
        var result = LogRedactor.Redact("devicePrivateKey=amzDevKey4");
        Assert.DoesNotContain("amzDevKey4", result);
    }

    [Fact]
    public void Redact_PemPrivateKeyBlock_BodyStripped()
    {
        var pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIBpemBodyAAA\nbbbCCCdddEEE\n-----END RSA PRIVATE KEY-----";
        var result = LogRedactor.Redact(pem);
        Assert.DoesNotContain("MIIBpemBodyAAA", result);
        Assert.DoesNotContain("bbbCCCdddEEE", result);
    }

    // --- Qobuz: user_auth_token ---

    [Fact]
    public void Redact_QobuzUserAuthToken_KeyValueForm_Stripped()
    {
        var result = LogRedactor.Redact("user_auth_token=qbzUat6");
        Assert.DoesNotContain("qbzUat6", result);
    }

    [Fact]
    public void Redact_QobuzUserAuthToken_JsonForm_Stripped()
    {
        var result = LogRedactor.Redact("{\"user_auth_token\":\"qbzUatJ8\"}");
        Assert.DoesNotContain("qbzUatJ8", result);
    }

    // --- Name-check contract: camelCase vendor keys are recognized as sensitive parameters ---
    // (These already pass via the case-insensitive Contains check; locked here so a refactor
    //  that tightens the term list to exact-match can't silently drop camelCase coverage.)

    [Theory]
    [InlineData("refreshToken")]
    [InlineData("accessToken")]
    [InlineData("developerToken")]
    [InlineData("MusicUserToken")]
    [InlineData("appSecret")]
    [InlineData("devicePrivateKey")]
    [InlineData("user_auth_token")]
    [InlineData("storeAuthenticationCookie")]
    public void IsSensitiveParameter_VendorKeys_True(string name)
    {
        Assert.True(LogRedactor.IsSensitiveParameter(name), $"{name} must be treated as sensitive");
        Assert.True(SensitiveKeys.IsSensitive(name), $"{name} must be treated as sensitive by SensitiveKeys");
    }

    // --- Non-secret identifier must NOT be over-redacted (honest scope boundary) ---
    // OAuth clientId is a public identifier, not a credential; redacting it would reduce
    // diagnostic value with no security benefit. This locks the decision to leave it visible.
    [Fact]
    public void Redact_ClientId_NotOverRedacted()
    {
        var result = LogRedactor.Redact("clientId=publicAppId123");
        Assert.Contains("publicAppId123", result);
    }
}
