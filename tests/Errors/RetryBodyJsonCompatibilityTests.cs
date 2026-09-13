using System;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Security.Llm;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Errors;

[Trait("Category", "Unit")]
public sealed class RetryBodyJsonCompatibilityTests
{
    [Fact]
    public void JsonExponent_IsConsumedAsOneNumericValue()
    {
        var error = Map("{\"retry_after\":1e5}");

        Assert.Equal(TimeSpan.FromSeconds(100000), error.RetryAfter);
    }

    [Theory]
    [InlineData("{\"error\":{\"retry_after\":5}}")]
    [InlineData("{\"retry_after\":5,\"retry_after\":5}")]
    [InlineData("{\"retry_after\":5,\"retry-after\":5}")]
    [InlineData("{\"retry_after\":5")]
    [InlineData("{\"RETRY_AFTER\":5}")]
    [InlineData("{\"retryafter\":5}")]
    [InlineData("\"retry_after: 5\"")]
    public void UnsupportedOrRejectedJson_DoesNotFallBackToLegacyText(string body)
    {
        Assert.Null(Map(body).RetryAfter);
    }

    [Fact]
    public void AtDepthLimit_RootHintRemainsSupported()
    {
        var error = Map(BuildNestedBody(9));

        Assert.Equal(TimeSpan.FromSeconds(5), error.RetryAfter);
    }

    [Fact]
    public void BeyondDepthLimit_RootHintIsRejected()
    {
        var error = Map(BuildNestedBody(10));

        Assert.Null(error.RetryAfter);
    }

    [Fact]
    public void OverLimitInput_IsRejectedBeforeLegacyMatching()
    {
        var body = "{\"retry_after\":5,\"padding\":\""
            + new string('x', LlmJsonSerializer.MaxJsonSize)
            + "\"}";

        Assert.Null(Map(body).RetryAfter);
    }

    [Fact]
    public void LegacyExponentGrammar_RemainsOneSecond()
    {
        var error = Map("retry_after: 1e5");

        Assert.Equal(TimeSpan.FromSeconds(1), error.RetryAfter);
    }

    [Theory]
    [InlineData("{\"retry_after\":null}")]
    [InlineData("{\"retry_after\":true}")]
    [InlineData("{\"retry_after\":\"5\"}")]
    [InlineData("{\"retry_after\":-1}")]
    public void UnsupportedJsonValueTypes_ReturnNoHint(string body)
    {
        Assert.Null(Map(body).RetryAfter);
    }

    private static RateLimitException Map(string body)
    {
        return Assert.IsType<RateLimitException>(
            LlmErrorMapper.MapHttpError("test-provider", 429, body));
    }

    private static string BuildNestedBody(int nestedObjectCount)
    {
        var body = "{\"retry_after\":5,\"nested\":";
        for (var i = 0; i < nestedObjectCount; i++)
        {
            body += "{\"level\":";
        }

        body += "0";
        for (var i = 0; i < nestedObjectCount; i++)
        {
            body += "}";
        }

        return body + "}";
    }
}
