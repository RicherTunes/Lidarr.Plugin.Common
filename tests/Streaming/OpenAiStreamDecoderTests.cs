// <copyright file="OpenAiStreamDecoderTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.IO;
using System.Text;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Streaming.Decoders;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Streaming;

public class OpenAiStreamDecoderTests
{
    private readonly OpenAiStreamDecoder _decoder = new();

    [Fact]
    public void DecoderId_ReturnsOpenAi()
    {
        Assert.Equal("openai", _decoder.DecoderId);
    }

    [Theory]
    [InlineData("text/event-stream", true)]
    [InlineData("text/event-stream; charset=utf-8", true)]
    [InlineData("TEXT/EVENT-STREAM", true)]
    [InlineData("application/json", false)]
    [InlineData("", false)]
    public void CanDecode_ReturnsExpected(string contentType, bool expected)
    {
        Assert.Equal(expected, _decoder.CanDecode(contentType));
    }

    [Fact]
    public async Task DecodeAsync_SingleChunk_ReturnsContent()
    {
        // Arrange
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""Hello""}}]}

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert - decoder adds a completion chunk when stream ends without [DONE]
        Assert.Equal(2, chunks.Length);
        Assert.Equal("Hello", chunks[0].ContentDelta);
        Assert.False(chunks[0].IsComplete);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task DecodeAsync_MultipleChunks_ReturnsAllContent()
    {
        // Arrange
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""Hello""}}]}

data: {""choices"":[{""delta"":{""content"":"" world""}}]}

data: {""choices"":[{""delta"":{""content"":""!""}}]}

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert - decoder adds a completion chunk when stream ends without [DONE]
        Assert.Equal(4, chunks.Length);
        Assert.Equal("Hello", chunks[0].ContentDelta);
        Assert.Equal(" world", chunks[1].ContentDelta);
        Assert.Equal("!", chunks[2].ContentDelta);
        Assert.True(chunks[3].IsComplete);
    }

    [Fact]
    public async Task DecodeAsync_DoneMarker_SetsIsComplete()
    {
        // Arrange
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""text""}}]}

data: [DONE]

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert
        Assert.Equal(2, chunks.Length);
        Assert.False(chunks[0].IsComplete);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task DecodeAsync_FinishReason_SetsIsComplete()
    {
        // Arrange
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""text""},""finish_reason"":""stop""}]}

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert - finish_reason causes immediate completion, no additional chunk
        Assert.Single(chunks);
        Assert.True(chunks[0].IsComplete);
        Assert.Equal("text", chunks[0].ContentDelta);
    }

    [Fact]
    public async Task DecodeAsync_UsageIncluded_ReturnsUsage()
    {
        // Arrange - with finish_reason set, decoder completes immediately
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""text""},""finish_reason"":""stop""}],""usage"":{""prompt_tokens"":10,""completion_tokens"":5,""total_tokens"":15}}

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert - finish_reason causes immediate completion with usage
        Assert.Single(chunks);
        Assert.True(chunks[0].IsComplete);
        var usage = chunks[0].FinalUsage;
        Assert.NotNull(usage);
        Assert.Equal(10, usage!.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
    }

    [Fact]
    public async Task DecodeAsync_EmptyChoices_SkipsChunk()
    {
        // Arrange
        var sseContent = @"data: {""choices"":[]}

data: {""choices"":[{""delta"":{""content"":""text""}}]}

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert - empty choices are skipped, but we get completion chunk at end
        Assert.Equal(2, chunks.Length);
        Assert.Equal("text", chunks[0].ContentDelta);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task DecodeAsync_MalformedJson_SkipsChunk()
    {
        // Arrange
        var sseContent = @"data: not valid json

data: {""choices"":[{""delta"":{""content"":""valid""}}]}

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert - malformed JSON is skipped, we get completion chunk at end
        Assert.Equal(2, chunks.Length);
        Assert.Equal("valid", chunks[0].ContentDelta);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task DecodeAsync_EmptyContent_SkipsChunk()
    {
        // Arrange
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""""}}]}

data: {""choices"":[{""delta"":{""content"":""text""}}]}

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert - empty content is skipped, we get completion chunk at end
        Assert.Equal(2, chunks.Length);
        Assert.Equal("text", chunks[0].ContentDelta);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task DecodeAsync_UsageOnlyChunk_CapturesUsage()
    {
        // Arrange - some APIs send usage in a separate chunk before [DONE]
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""text""}}]}

data: {""usage"":{""prompt_tokens"":10,""completion_tokens"":5}}

data: [DONE]

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert - content chunk, then completion with captured usage
        Assert.Equal(2, chunks.Length);
        Assert.Equal("text", chunks[0].ContentDelta);
        Assert.True(chunks[1].IsComplete);
        var usage = chunks[1].FinalUsage;
        Assert.NotNull(usage);
        Assert.Equal(10, usage!.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
    }

    [Fact]
    public async Task DecodeAsync_StreamEndsWithoutDone_YieldsCompletionChunk()
    {
        // Arrange
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""text""}}]}

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert
        Assert.Equal(2, chunks.Length);
        Assert.False(chunks[0].IsComplete);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task DecodeAsync_NoUsageProvided_FinalUsageIsNull()
    {
        // Arrange - stream with no usage info (usage is optional per OpenAI spec)
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""Hello world""}}]}

data: {""choices"":[{""delta"":{},""finish_reason"":""stop""}]}

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert - decoder handles missing usage gracefully
        Assert.Equal(2, chunks.Length);
        Assert.Equal("Hello world", chunks[0].ContentDelta);
        Assert.True(chunks[1].IsComplete);
        Assert.Null(chunks[1].FinalUsage); // No usage in either chunk
    }

    [Fact]
    public async Task DecodeAsync_UsageOnlyAtEnd_CapturesFromFinalChunk()
    {
        // Arrange - usage only appears with finish_reason (common pattern)
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""Hello""}}]}

data: {""choices"":[{""delta"":{""content"":"" world""}}]}

data: {""choices"":[{""delta"":{},""finish_reason"":""stop""}],""usage"":{""prompt_tokens"":25,""completion_tokens"":10,""total_tokens"":35}}

";
        using var stream = CreateStream(sseContent);

        // Act
        var chunks = await CollectChunksAsync(stream);

        // Assert
        Assert.Equal(3, chunks.Length);
        Assert.Equal("Hello", chunks[0].ContentDelta);
        Assert.Equal(" world", chunks[1].ContentDelta);
        Assert.True(chunks[2].IsComplete);
        var usage = chunks[2].FinalUsage;
        Assert.NotNull(usage);
        Assert.Equal(25, usage!.InputTokens);
        Assert.Equal(10, usage.OutputTokens);
    }

    private static MemoryStream CreateStream(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    private async Task<LlmStreamChunk[]> CollectChunksAsync(Stream stream)
    {
        var chunks = new System.Collections.Generic.List<LlmStreamChunk>();
        await foreach (var chunk in _decoder.DecodeAsync(stream))
        {
            chunks.Add(chunk);
        }

        return chunks.ToArray();
    }
}
