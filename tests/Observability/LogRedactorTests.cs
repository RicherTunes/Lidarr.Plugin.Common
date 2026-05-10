using System;
using System.Collections.Generic;
using Lidarr.Plugin.Common.Observability;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Observability;

public class LogRedactorTests
{
    [Fact]
    public void Redact_OpenAiKey_RedactedByStructuredPattern()
    {
        var input = "key=sk-abcdef1234567890ABCDEF1234567890";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("sk-abcdef1234567890ABCDEF1234567890", result);
        Assert.Contains(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void Redact_AnthropicKey_RedactedByStructuredPattern()
    {
        var input = "Auth: sk-ant-api03-abcdef1234567890";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("sk-ant-api03-abcdef1234567890", result);
        Assert.Contains(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void Redact_GoogleKey_RedactedByStructuredPattern()
    {
        var input = "GOOGLE_API_KEY=AIzaSyBXXxXxXxXxXxXxXxXxXxXxXxXxXxXxXxX";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("AIzaSyBXXxXxXxXxXxXxXxXxXxXxXxXxXxXxXxX", result);
        Assert.Contains(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void Redact_BearerToken_RedactedByStructuredPattern()
    {
        var input = "header: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.payload.sig", result);
        Assert.Contains("Bearer", result);
        Assert.Contains(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void Redact_GenericLongOpaqueToken_Redacted()
    {
        // 40-char opaque token (no structured prefix) — should be caught by generic pattern.
        const string opaque = "abcdef1234567890ABCDEFGHIJKLMNOPQRSTUVWX";
        var input = $"token value: {opaque}";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain(opaque, result);
        Assert.Contains(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void Redact_HyphenatedUuid_NotRedacted()
    {
        // Standard hyphenated UUIDs do not match \b[A-Za-z0-9]{32,}\b due to hyphens,
        // and don't match the CC pattern because of the hex letters.
        // The all-zero UUID is degenerate and would be caught by the CC pattern, which
        // is an accepted trade-off — real UUIDs in the wild contain a-f letters.
        const string uuid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        var input = $"id: {uuid}";
        var result = LogRedactor.Redact(input);
        Assert.Contains(uuid, result);
    }

    [Fact]
    public void Redact_NonHyphenatedHash_RedactedAsAcceptedFalsePositive()
    {
        // 40-char Git SHA. The generic pattern intentionally redacts these — documented trade-off.
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var input = $"commit {sha}";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain(sha, result);
    }

    [Fact]
    public void Redact_ShortString_NotRedacted()
    {
        const string shortVal = "abc123def456";
        var result = LogRedactor.Redact($"id: {shortVal}");
        Assert.Contains(shortVal, result);
    }

    [Fact]
    public void Redact_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LogRedactor.Redact(null));
        Assert.Equal(string.Empty, LogRedactor.Redact(string.Empty));
    }

    [Fact]
    public void Redact_StructuredPatternClaimedBeforeGeneric()
    {
        // Verify ordering: an OpenAI key replacement uses the OpenAI redaction, not double-redaction.
        var input = "sk-abcdef1234567890ABCDEF1234567890";
        var result = LogRedactor.Redact(input);
        // After OpenAI pattern matches, the result is REDACTED (no remaining long alphanumeric to re-match).
        Assert.Equal(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void Redact_NestedJsonApiKey_ValueRedacted()
    {
        // Short opaque value that wouldn't trip structured patterns or 32-char threshold.
        var input = "{\"api_key\":\"shortabc123\",\"other\":\"safe\"}";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("shortabc123", result);
        Assert.Contains("api_key", result);
        Assert.Contains(LogRedactor.REDACTED, result);
        Assert.Contains("safe", result); // non-sensitive sibling preserved
    }

    [Fact]
    public void Redact_NestedJsonAccessToken_ValueRedacted()
    {
        var input = "request body: {\"access_token\":\"opaque-vendor-token\",\"expires_in\":3600}";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("opaque-vendor-token", result);
        Assert.Contains("access_token", result);
        Assert.Contains("expires_in", result);
    }

    [Fact]
    public void Redact_NestedJsonClientSecret_HyphenatedKeyVariant()
    {
        var input = "{\"client-secret\":\"clsec-xyz\"}";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("clsec-xyz", result);
    }

    [Fact]
    public void Redact_NestedJsonNonSensitiveKey_ValuePreserved()
    {
        // username is not in the JSON-sensitive list, so its value is left alone (unless caught by other patterns).
        var input = "{\"username\":\"alice\",\"display\":\"Alice Doe\"}";
        var result = LogRedactor.Redact(input);
        Assert.Contains("alice", result);
        Assert.Contains("Alice Doe", result);
    }

    [Fact]
    public void Redact_ChainedPatterns_AllApply()
    {
        // Bearer + JSON sensitive + generic all in one string.
        var input = "Authorization: Bearer eyJhbGc.payload.sig | body={\"api_key\":\"k1\"} | sha=0123456789abcdef0123456789abcdef01234567";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("eyJhbGc.payload.sig", result);
        Assert.DoesNotContain("\"k1\"", result);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef01234567", result);
    }

    [Fact]
    public void Redact_MalformedJson_DoesNotThrow()
    {
        // Unterminated string after sensitive key; regex should simply not match, not crash.
        var input = "{\"api_key\":\"unterminated";
        var result = LogRedactor.Redact(input);
        Assert.NotNull(result);
    }

    [Fact]
    public void Redact_VeryShortToken_NotRedacted()
    {
        // Sub-32-char strings that aren't structured shouldn't be touched.
        var input = "sessionId=abc12";
        var result = LogRedactor.Redact(input);
        Assert.Contains("abc12", result);
    }

    [Fact]
    public void Redact_BasicAuthHeader_ValueFullyRedacted()
    {
        // Basic auth: Authorization scheme is "Basic", followed by base64-encoded user:pass.
        // The previous \S+ pattern only consumed "Basic" and left the credential in cleartext.
        var input = "Authorization: Basic dXNlcm5hbWU6cGFzc3dvcmQ=";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("dXNlcm5hbWU6cGFzc3dvcmQ=", result);
        Assert.Contains("Authorization", result);
        Assert.Contains(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void Redact_AuthHeader_StopsAtPipeDelimiter()
    {
        // Multi-field log line — auth pattern should not consume past the pipe separator.
        var input = "Authorization: Basic dXNlcjpwd2Q= | next: safe-field-value";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("dXNlcjpwd2Q=", result);
        Assert.Contains("safe-field-value", result);
    }

    [Fact]
    public void Redact_CookieHeader_RedactsShortSessionId()
    {
        // 12-char session id — well below the 32-char generic threshold.
        var input = "Cookie: session_id=xyz789abc123; path=/";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("xyz789abc123", result);
        Assert.Contains("Cookie", result);
        Assert.Contains(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void Redact_SetCookieHeader_RedactsJSessionId()
    {
        // 20-char JSESSIONID — also below 32-char threshold.
        var input = "Set-Cookie: JSESSIONID=1A2B3C4D5E6F7G8H9I0J; HttpOnly";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("1A2B3C4D5E6F7G8H9I0J", result);
        Assert.Contains("Set-Cookie", result);
        Assert.Contains(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void Redact_EmailAddress_Redacted()
    {
        var input = "Contact admin@example.com or user.test+tag@sub.example.co for help";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("admin@example.com", result);
        Assert.DoesNotContain("user.test+tag@sub.example.co", result);
        Assert.Contains(LogRedactor.REDACTED, result);
        Assert.Contains("Contact", result);
    }

    [Fact]
    public void Redact_IPv4Address_Redacted()
    {
        var input = "Server at 192.168.1.100 forwarded by 10.0.0.1";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("192.168.1.100", result);
        Assert.DoesNotContain("10.0.0.1", result);
    }

    [Fact]
    public void Redact_CreditCardLikeSequence_Redacted()
    {
        var input = "card on file: 4111-1111-1111-1111 expires soon";
        var result = LogRedactor.Redact(input);
        Assert.DoesNotContain("4111-1111-1111-1111", result);
    }

    [Fact]
    public void Redact_NormalProseAndMetrics_Preserved()
    {
        // Ensure PII redaction doesn't eat ordinary log prose.
        var input = "Provider openai returned 5 recommendations in 250ms";
        var result = LogRedactor.Redact(input);
        Assert.Contains("openai", result);
        Assert.Contains("5 recommendations", result);
        Assert.Contains("250ms", result);
    }

    [Fact]
    public void RedactException_NullException_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LogRedactor.RedactException(null));
    }

    [Fact]
    public void RedactException_SingleException_RedactsMessage()
    {
        var ex = new InvalidOperationException("OAuth failed for sk-abcdef1234567890ABCDEF1234567890 endpoint");
        var result = LogRedactor.RedactException(ex);
        Assert.Contains("InvalidOperationException", result);
        Assert.DoesNotContain("sk-abcdef1234567890ABCDEF1234567890", result);
        Assert.Contains(LogRedactor.REDACTED, result);
    }

    [Fact]
    public void RedactException_NestedInner_WalksChainAndRedacts()
    {
        var inner = new System.Net.Http.HttpRequestException("Bearer eyJhbGc.payload.signature was rejected");
        var outer = new InvalidOperationException("token refresh failed", inner);
        var result = LogRedactor.RedactException(outer);
        Assert.Contains("InvalidOperationException", result);
        Assert.Contains("HttpRequestException", result);
        Assert.Contains("-->", result);
        Assert.DoesNotContain("eyJhbGc.payload.signature", result);
    }

    [Theory]
    [InlineData("apiKey", true)]
    [InlineData("api_key", true)]
    [InlineData("Authorization", true)]
    [InlineData("password", true)]
    [InlineData("client_secret", true)]
    [InlineData("session_id", true)]
    [InlineData("refresh_token", true)]
    [InlineData("X-API-Key", true)]
    [InlineData("custom_auth_field", true)]   // contains "auth"
    [InlineData("user_credential", true)]      // contains "credential"
    [InlineData("user_password_hash", true)]   // contains "password"
    [InlineData("display_name", false)]
    [InlineData("user_id", false)]
    [InlineData("count", false)]
    public void IsSensitiveParameter_ClassifiesNamesCorrectly(string name, bool expected)
    {
        Assert.Equal(expected, LogRedactor.IsSensitiveParameter(name));
    }

    [Fact]
    public void IsSensitiveParameter_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(LogRedactor.IsSensitiveParameter(null));
        Assert.False(LogRedactor.IsSensitiveParameter(""));
    }

    [Fact]
    public void RedactDictionary_RedactsSensitiveKeysAndScansStringValues()
    {
        var input = new Dictionary<string, object>
        {
            ["api_key"] = "sk-abcdef1234567890ABCDEF1234567890",
            ["username"] = "alice",                                // not sensitive name, not a token
            ["body"] = "Authorization: Bearer eyJhbGc.payload.sig", // string value gets scanned
            ["count"] = 42,                                         // non-string preserved as-is
        };
        var result = LogRedactor.RedactDictionary(input);

        Assert.Equal(LogRedactor.REDACTED, result["api_key"]);
        Assert.Equal("alice", result["username"]);
        Assert.DoesNotContain("eyJhbGc.payload.sig", (string)result["body"]);
        Assert.Equal(42, result["count"]);
    }

    [Fact]
    public void RedactDictionary_NullSource_ReturnsEmptyDictionary()
    {
        var result = LogRedactor.RedactDictionary(null);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void RedactException_DeeplyNested_StopsAtMaxDepth()
    {
        // Build a chain of 12 exceptions; helper should cap at 8 frames and not stack-overflow.
        Exception? curr = null;
        for (var i = 0; i < 12; i++)
        {
            curr = new InvalidOperationException($"level {i}", curr);
        }
        var result = LogRedactor.RedactException(curr);
        Assert.NotNull(result);
        // Max depth = 8, so we should see exactly 7 separators.
        var arrowCount = (result.Length - result.Replace(" --> ", string.Empty).Length) / " --> ".Length;
        Assert.Equal(7, arrowCount);
    }
}
