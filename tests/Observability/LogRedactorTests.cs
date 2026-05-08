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
        // Standard hyphenated UUIDs do not match \b[A-Za-z0-9]{32,}\b due to hyphens.
        const string uuid = "00000000-0000-0000-0000-000000000000";
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
}
