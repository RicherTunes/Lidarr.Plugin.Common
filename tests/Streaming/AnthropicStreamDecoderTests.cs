// <copyright file="AnthropicStreamDecoderTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Streaming.Decoders;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Streaming;

public class AnthropicStreamDecoderTests
{
    private readonly AnthropicStreamDecoder _decoder = new();

    [Fact]
    public void DecoderId_IsAnthropic()
    {
        Assert.Equal("anthropic", _decoder.DecoderId);
    }

    [Fact]
    public void SupportedProviderIds_IncludesAnthropicAndClaude()
    {
        Assert.Contains("anthropic", _decoder.SupportedProviderIds);
        Assert.Contains("claude", _decoder.SupportedProviderIds);
    }

    [Theory]
    [InlineData("text/event-stream", true)]
    [InlineData("text/event-stream; charset=utf-8", true)]
    [InlineData("TEXT/EVENT-STREAM", true)]
    [InlineData("application/json", false)]
    [InlineData("", false)]
    public void CanDecode_MatchesSseContentType(string contentType, bool expected)
    {
        Assert.Equal(expected, _decoder.CanDecode(contentType));
    }

    [Fact]
    public void CanDecodeForProvider_AnthropicSse_True()
    {
        Assert.True(_decoder.CanDecodeForProvider("anthropic", "text/event-stream"));
        Assert.True(_decoder.CanDecodeForProvider("claude", "text/event-stream"));
    }

    [Fact]
    public void CanDecodeForProvider_NonAnthropic_False()
    {
        Assert.False(_decoder.CanDecodeForProvider("openai", "text/event-stream"));
    }

    [Fact]
    public async Task DecodeAsync_HappyPath_YieldsContentDeltas_AndTerminalUsage()
    {
        // Anthropic message stream: message_start (input usage) -> content_block_delta(text_delta) ... -> message_delta (final output usage) -> message_stop
        var sse = "event: message_start\n"
            + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":12,\"output_tokens\":0}}}\n"
            + "\n"
            + "event: content_block_delta\n"
            + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n"
            + "\n"
            + "event: content_block_delta\n"
            + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" world\"}}\n"
            + "\n"
            + "event: message_delta\n"
            + "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":5}}\n"
            + "\n"
            + "event: message_stop\n"
            + "data: {\"type\":\"message_stop\"}\n"
            + "\n";

        var chunks = await CollectAsync(sse);

        Assert.Equal(3, chunks.Length);
        Assert.Equal("Hello", chunks[0].ContentDelta);
        Assert.False(chunks[0].IsComplete);
        Assert.Equal(" world", chunks[1].ContentDelta);
        Assert.True(chunks[2].IsComplete);
        Assert.NotNull(chunks[2].FinalUsage);
        Assert.Equal(12, chunks[2].FinalUsage!.InputTokens);
        Assert.Equal(5, chunks[2].FinalUsage!.OutputTokens);
    }

    [Fact]
    public async Task DecodeAsync_ThinkingDelta_RoutesToReasoningDelta()
    {
        var sse = "event: message_start\n"
            + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_2\",\"usage\":{\"input_tokens\":3}}}\n"
            + "\n"
            + "event: content_block_delta\n"
            + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"considering...\"}}\n"
            + "\n"
            + "event: content_block_delta\n"
            + "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Final answer.\"}}\n"
            + "\n"
            + "event: message_stop\n"
            + "data: {\"type\":\"message_stop\"}\n"
            + "\n";

        var chunks = await CollectAsync(sse);

        Assert.Equal(3, chunks.Length);
        Assert.Equal("considering...", chunks[0].ReasoningDelta);
        Assert.Null(chunks[0].ContentDelta);
        Assert.Equal("Final answer.", chunks[1].ContentDelta);
        Assert.Null(chunks[1].ReasoningDelta);
        Assert.True(chunks[2].IsComplete);
    }

    [Fact]
    public async Task DecodeAsync_NoMessageStop_StillYieldsTerminalChunk()
    {
        var sse = "event: message_start\n"
            + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_3\"}}\n"
            + "\n"
            + "event: content_block_delta\n"
            + "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Done\"}}\n"
            + "\n";

        var chunks = await CollectAsync(sse);

        Assert.Equal(2, chunks.Length);
        Assert.Equal("Done", chunks[0].ContentDelta);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task DecodeAsync_MalformedJsonFrame_Skipped()
    {
        var sse = "event: content_block_delta\n"
            + "data: not-valid-json\n"
            + "\n"
            + "event: content_block_delta\n"
            + "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"After bad frame\"}}\n"
            + "\n"
            + "event: message_stop\n"
            + "data: {\"type\":\"message_stop\"}\n"
            + "\n";

        var chunks = await CollectAsync(sse);

        Assert.Equal(2, chunks.Length);
        Assert.Equal("After bad frame", chunks[0].ContentDelta);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task DecodeAsync_EventTypeOmitted_FallsBackToPayloadType()
    {
        // Some servers omit "event:" lines and rely solely on the JSON "type" field.
        var sse = "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":7}}}\n"
            + "\n"
            + "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n"
            + "\n"
            + "data: {\"type\":\"message_stop\"}\n"
            + "\n";

        var chunks = await CollectAsync(sse);

        Assert.Equal(2, chunks.Length);
        Assert.Equal("Hi", chunks[0].ContentDelta);
        Assert.True(chunks[1].IsComplete);
        Assert.NotNull(chunks[1].FinalUsage);
        Assert.Equal(7, chunks[1].FinalUsage!.InputTokens);
    }

    [Fact]
    public async Task DecodeAsync_NoUsageAtAll_FinalUsageIsNull()
    {
        var sse = "event: content_block_delta\n"
            + "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"x\"}}\n"
            + "\n"
            + "event: message_stop\n"
            + "data: {\"type\":\"message_stop\"}\n"
            + "\n";

        var chunks = await CollectAsync(sse);

        Assert.True(chunks.Last().IsComplete);
        Assert.Null(chunks.Last().FinalUsage);
    }

    private async Task<LlmStreamChunk[]> CollectAsync(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var list = new List<LlmStreamChunk>();
        await foreach (var chunk in _decoder.DecodeAsync(stream))
        {
            list.Add(chunk);
        }

        return list.ToArray();
    }
}
