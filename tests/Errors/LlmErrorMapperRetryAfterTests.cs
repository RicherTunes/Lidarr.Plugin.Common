using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Lidarr.Plugin.Common.Errors;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Errors;

/// <summary>
/// Tests for the Phase 5e <see cref="LlmErrorMapper"/> Retry-After propagation overloads.
/// </summary>
[Trait("Category", "Unit")]
public class LlmErrorMapperRetryAfterTests
{
    [Fact]
    public void MapHttpError_HttpResponseMessage_ExtractsRetryAfterDeltaHeader()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));

        var ex = LlmErrorMapper.MapHttpError("anthropic", resp);

        var rate = Assert.IsType<RateLimitException>(ex);
        Assert.Equal(TimeSpan.FromSeconds(45), rate.RetryAfter);
    }

    [Fact]
    public void MapHttpError_HttpResponseMessage_ExtractsRetryAfterDateHeader()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var future = DateTimeOffset.UtcNow.AddSeconds(30);
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(future);

        var ex = LlmErrorMapper.MapHttpError("openai", resp);

        var rate = Assert.IsType<RateLimitException>(ex);
        Assert.NotNull(rate.RetryAfter);
        // Allow small tolerance for clock drift between header creation and assertion.
        Assert.True(rate.RetryAfter!.Value.TotalSeconds >= 25 && rate.RetryAfter.Value.TotalSeconds <= 35);
    }

    [Fact]
    public void MapHttpError_ExplicitRetryAfter_OverridesBodyParse()
    {
        // Body suggests 5s, header overrides to 60s — header wins.
        var ex = LlmErrorMapper.MapHttpError(
            providerId: "test",
            statusCode: 429,
            responseBody: "{\"retry_after\":5}",
            retryAfter: TimeSpan.FromSeconds(60),
            inner: null);

        var rate = Assert.IsType<RateLimitException>(ex);
        Assert.Equal(TimeSpan.FromSeconds(60), rate.RetryAfter);
    }

    [Fact]
    public void MapHttpError_NoRetryAfter_FallsBackToBodyParse()
    {
        var ex = LlmErrorMapper.MapHttpError(
            providerId: "gemini",
            statusCode: 429,
            responseBody: "{\"retry_after\":7}",
            retryAfter: null,
            inner: null);

        var rate = Assert.IsType<RateLimitException>(ex);
        Assert.Equal(TimeSpan.FromSeconds(7), rate.RetryAfter);
    }

    [Fact]
    public void MapHttpError_HttpResponseMessage_NullResponse_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LlmErrorMapper.MapHttpError("p", (HttpResponseMessage)null!));
    }

    [Fact]
    public void ParseRetryAfterHeader_NullHeader_ReturnsNull()
    {
        Assert.Null(LlmErrorMapper.ParseRetryAfterHeader(null));
    }

    [Fact]
    public void ParseRetryAfterHeader_DeltaHeader_ReturnsDelta()
    {
        var header = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
        Assert.Equal(TimeSpan.FromSeconds(12), LlmErrorMapper.ParseRetryAfterHeader(header));
    }

    [Fact]
    public void ParseRetryAfterHeader_PastDate_ReturnsZero()
    {
        var header = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-30));
        Assert.Equal(TimeSpan.Zero, LlmErrorMapper.ParseRetryAfterHeader(header));
    }

    [Fact]
    public void MapHttpError_NonRateLimitStatus_LeavesRetryAfterNull()
    {
        // 503 should not carry retry-after even when supplied — it's a different exception type.
        var ex = LlmErrorMapper.MapHttpError(
            providerId: "test",
            statusCode: 503,
            responseBody: null,
            retryAfter: TimeSpan.FromSeconds(30),
            inner: null);

        Assert.IsType<ProviderException>(ex);
        Assert.Null(ex.RetryAfter); // base class default — RateLimitException is the only path that sets it
    }
}
